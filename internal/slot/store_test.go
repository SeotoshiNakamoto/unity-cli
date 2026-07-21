package slot

import (
	"context"
	"errors"
	"path/filepath"
	"testing"
	"time"
)

func TestStoreQueueClaimAndComplete(t *testing.T) {
	store, err := OpenStore(filepath.Join(t.TempDir(), "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = store.Close() }()
	ctx := context.Background()
	job := Job{
		ID: "job-1", Project: "ProjectD", Repository: `D:\ProjectD`, SHA: "abc",
		Suite: "compile", Affinity: "task-1", CreatedAt: time.Now().UTC(),
	}
	if err := store.Enqueue(ctx, job); err != nil {
		t.Fatal(err)
	}
	claimed, err := store.Claim(ctx, "ProjectD", "verify-01", "", false)
	if err != nil || claimed == nil {
		t.Fatalf("claim: job=%v err=%v", claimed, err)
	}
	if claimed.Status != JobRunning || claimed.SlotID != "verify-01" {
		t.Fatalf("claimed state: %+v", claimed)
	}
	result := &Result{Passed: false, TestedSHA: "abc", SlotID: "verify-01"}
	if err := store.Complete(ctx, job.ID, JobFailed, result, errors.New("compile failed"), "job.log"); err != nil {
		t.Fatal(err)
	}
	finished, err := store.Get(ctx, job.ID)
	if err != nil {
		t.Fatal(err)
	}
	if finished.Status != JobFailed || finished.Error != "compile failed" || finished.Result == nil {
		t.Fatalf("finished state: %+v", finished)
	}
}

func TestAffinityLeaseKeepsRetryOnOwningSlot(t *testing.T) {
	store, err := OpenStore(filepath.Join(t.TempDir(), "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = store.Close() }()
	ctx := context.Background()
	if err := store.SetAffinityLease(ctx, "ProjectD", "task-1", "verify-02", time.Now().Add(time.Minute)); err != nil {
		t.Fatal(err)
	}
	job := Job{ID: "retry", Project: "ProjectD", Repository: `D:\ProjectD`, SHA: "def", Suite: "compile", Affinity: "task-1", CreatedAt: time.Now().UTC()}
	if err := store.Enqueue(ctx, job); err != nil {
		t.Fatal(err)
	}
	claimed, err := store.Claim(ctx, "ProjectD", "verify-01", "", false)
	if err != nil || claimed != nil {
		t.Fatalf("non-owner claim: job=%v err=%v", claimed, err)
	}
	claimed, err = store.Claim(ctx, "ProjectD", "verify-02", "task-1", true)
	if err != nil || claimed == nil || claimed.ID != "retry" {
		t.Fatalf("owner claim: job=%v err=%v", claimed, err)
	}
}
