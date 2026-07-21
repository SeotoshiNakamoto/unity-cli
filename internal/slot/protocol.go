package slot

import "time"

const ProtocolVersion = 1

const (
	JobQueued   = "queued"
	JobRunning  = "running"
	JobPassed   = "passed"
	JobFailed   = "failed"
	JobCanceled = "canceled"
)

type Request struct {
	Version int            `json:"version"`
	Action  string         `json:"action"`
	Submit  *SubmitRequest `json:"submit,omitempty"`
	JobID   string         `json:"jobId,omitempty"`
}

type SubmitRequest struct {
	Repository string `json:"repository"`
	SHA        string `json:"sha"`
	Suite      string `json:"suite"`
	Affinity   string `json:"affinity,omitempty"`
}

type Response struct {
	OK     bool          `json:"ok"`
	Error  string        `json:"error,omitempty"`
	Job    *Job          `json:"job,omitempty"`
	Jobs   []Job         `json:"jobs"`
	Doctor *DoctorReport `json:"doctor,omitempty"`
}

type Job struct {
	ID          string    `json:"id"`
	Project     string    `json:"project"`
	Repository  string    `json:"repository"`
	SHA         string    `json:"sha"`
	Suite       string    `json:"suite"`
	Affinity    string    `json:"affinity"`
	Status      string    `json:"status"`
	SlotID      string    `json:"slotId,omitempty"`
	CreatedAt   time.Time `json:"createdAt"`
	StartedAt   time.Time `json:"startedAt,omitempty"`
	FinishedAt  time.Time `json:"finishedAt,omitempty"`
	Result      *Result   `json:"result,omitempty"`
	Error       string    `json:"error,omitempty"`
	LogPath     string    `json:"logPath,omitempty"`
	ToolVersion string    `json:"toolVersion,omitempty"`
}

type Result struct {
	Passed       bool         `json:"passed"`
	TestedSHA    string       `json:"testedSha"`
	SlotID       string       `json:"slotId"`
	UnityProject string       `json:"unityProject"`
	Steps        []StepResult `json:"steps"`
}

type StepResult struct {
	Name       string `json:"name"`
	ExitCode   int    `json:"exitCode"`
	DurationMS int64  `json:"durationMs"`
	Output     string `json:"output,omitempty"`
	Error      string `json:"error,omitempty"`
}

type DoctorReport struct {
	ConfigPath string       `json:"configPath"`
	StatePath  string       `json:"statePath"`
	Endpoint   string       `json:"endpoint"`
	Slots      []SlotDoctor `json:"slots"`
	Healthy    bool         `json:"healthy"`
}

type SlotDoctor struct {
	ID                     string `json:"id"`
	Project                string `json:"project"`
	Worktree               string `json:"worktree"`
	GitCommonDir           string `json:"gitCommonDir,omitempty"`
	UnityProject           string `json:"unityProject,omitempty"`
	UnityCLI               string `json:"unityCli,omitempty"`
	UnityExecutable        string `json:"unityExecutable,omitempty"`
	DesktopIndex           *int   `json:"desktopIndex,omitempty"`
	DesktopName            string `json:"desktopName,omitempty"`
	DesktopFallbackFromEnd *int   `json:"desktopFallbackFromEnd,omitempty"`
	ResolvedDesktopIndex   *int   `json:"resolvedDesktopIndex,omitempty"`
	DesktopSelectionSource string `json:"desktopSelectionSource,omitempty"`
	Healthy                bool   `json:"healthy"`
	Error                  string `json:"error,omitempty"`
}
