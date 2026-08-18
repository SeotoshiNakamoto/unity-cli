package cmd

import (
	"fmt"
	"regexp"
	"strings"
)

const allowDeferredCodeFlag = "allow-deferred-code"

var deferredExecPatterns = []struct {
	label string
	re    *regexp.Regexp
}{
	{"async/await", regexp.MustCompile(`\b(?:async|await)\b`)},
	{"task continuation", regexp.MustCompile(`\b(?:Task|ValueTask|UniTask|Awaitable|TaskCompletionSource)\b`)},
	{"coroutine", regexp.MustCompile(`\b(?:Coroutine|StartCoroutine|EditorCoroutineUtility|WaitForSeconds|WaitUntil|WaitWhile)\b`)},
	{"Unity async operation", regexp.MustCompile(`\b(?:AsyncOperation|LoadSceneAsync|UnloadSceneAsync|LoadAsync|InstantiateAsync|AsyncGPUReadback|SendWebRequest)\b`)},
	{"EditorApplication deferred callback", regexp.MustCompile(`\bEditorApplication\s*\.\s*(?:delayCall|update)\b`)},
}

// validateExecDeferredPolicy rejects code that can outlive the synchronous
// ExecuteCsharp invocation. The transport-level --async flag remains separate:
// it moves the whole command into a pollable unity-cli job, but does not make
// deferred callbacks safe. The CLI-only override is removed before dispatch.
func validateExecDeferredPolicy(params map[string]interface{}) error {
	allow, err := takeBoolParam(params, allowDeferredCodeFlag)
	if err != nil {
		return err
	}
	if allow {
		return nil
	}

	code := stripCSharpCommentsAndLiterals(execCode(params))
	for _, pattern := range deferredExecPatterns {
		if pattern.re.MatchString(code) {
			return fmt.Errorf("exec blocks code that can outlive the request: found %s; use --%s only when the deferred lifetime is intentional", pattern.label, allowDeferredCodeFlag)
		}
	}
	return nil
}

func takeBoolParam(params map[string]interface{}, key string) (bool, error) {
	value, exists := params[key]
	delete(params, key)
	if !exists {
		return false, nil
	}
	allowed, ok := value.(bool)
	if !ok {
		return false, fmt.Errorf("--%s must be a boolean flag", key)
	}
	return allowed, nil
}

func execCode(params map[string]interface{}) string {
	if code, ok := params["code"].(string); ok {
		return code
	}
	switch args := params["args"].(type) {
	case []string:
		if len(args) > 0 {
			return args[0]
		}
	case []interface{}:
		if len(args) > 0 {
			if code, ok := args[0].(string); ok {
				return code
			}
		}
	}
	return ""
}

// stripCSharpCommentsAndLiterals keeps token boundaries while removing text
// that would otherwise make the lightweight safety scan report false matches.
func stripCSharpCommentsAndLiterals(code string) string {
	const (
		normal = iota
		lineComment
		blockComment
		quotedString
		verbatimString
		charLiteral
	)

	var out strings.Builder
	out.Grow(len(code))
	state := normal
	escaped := false

	for i := 0; i < len(code); i++ {
		ch := code[i]
		next := byte(0)
		if i+1 < len(code) {
			next = code[i+1]
		}

		switch state {
		case normal:
			switch {
			case ch == '/' && next == '/':
				out.WriteString("  ")
				i++
				state = lineComment
			case ch == '/' && next == '*':
				out.WriteString("  ")
				i++
				state = blockComment
			case ch == '@' && next == '"':
				out.WriteString("  ")
				i++
				state = verbatimString
			case ch == '"':
				out.WriteByte(' ')
				state = quotedString
				escaped = false
			case ch == '\'':
				out.WriteByte(' ')
				state = charLiteral
				escaped = false
			default:
				out.WriteByte(ch)
			}
		case lineComment:
			if ch == '\n' {
				out.WriteByte('\n')
				state = normal
			} else {
				out.WriteByte(' ')
			}
		case blockComment:
			if ch == '*' && next == '/' {
				out.WriteString("  ")
				i++
				state = normal
			} else if ch == '\n' {
				out.WriteByte('\n')
			} else {
				out.WriteByte(' ')
			}
		case quotedString, charLiteral:
			if ch == '\n' {
				out.WriteByte('\n')
			} else {
				out.WriteByte(' ')
			}
			if escaped {
				escaped = false
			} else if ch == '\\' {
				escaped = true
			} else if (state == quotedString && ch == '"') || (state == charLiteral && ch == '\'') {
				state = normal
			}
		case verbatimString:
			if ch == '"' && next == '"' {
				out.WriteString("  ")
				i++
			} else {
				if ch == '\n' {
					out.WriteByte('\n')
				} else {
					out.WriteByte(' ')
				}
				if ch == '"' {
					state = normal
				}
			}
		}
	}

	return out.String()
}
