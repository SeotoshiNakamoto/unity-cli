package slot

import (
	"reflect"
	"testing"
)

func TestDesktopTargetPrefersNameWithFallback(t *testing.T) {
	fallback := 2
	target := desktopTargetFor(SlotConfig{
		DesktopName:            "LLM 유니티 슬롯 1",
		DesktopFallbackFromEnd: &fallback,
	})
	want := []string{"--desktop-name", "LLM 유니티 슬롯 1", "--fallback-from-end", "2"}
	if got := target.args(); !reflect.DeepEqual(got, want) {
		t.Fatalf("desktop target args = %#v, want %#v", got, want)
	}
}

func TestDesktopTargetKeepsLegacyIndex(t *testing.T) {
	index := 3
	target := desktopTargetFor(SlotConfig{DesktopIndex: &index})
	want := []string{"--desktop", "3"}
	if got := target.args(); !reflect.DeepEqual(got, want) {
		t.Fatalf("desktop target args = %#v, want %#v", got, want)
	}
}

func TestDecodeDesktopDoctorOutputIgnoresDebugNoise(t *testing.T) {
	output := []byte("Retry the function after ComNotInitialized\n{\"desktopCount\":4,\"currentDesktop\":0,\"selectedDesktop\":3,\"selectionSource\":\"name\"}\n")
	result, err := decodeDesktopDoctorOutput(output)
	if err != nil {
		t.Fatal(err)
	}
	if result.SelectedDesktop == nil || *result.SelectedDesktop != 3 {
		t.Fatalf("selected desktop = %#v, want 3", result.SelectedDesktop)
	}
}
