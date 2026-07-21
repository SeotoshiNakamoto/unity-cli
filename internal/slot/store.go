package slot

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	_ "modernc.org/sqlite"
)

type Store struct {
	db *sql.DB
}

func OpenStore(path string) (*Store, error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return nil, fmt.Errorf("create slot state directory: %w", err)
	}
	db, err := sql.Open("sqlite", path)
	if err != nil {
		return nil, fmt.Errorf("open slot database: %w", err)
	}
	db.SetMaxOpenConns(1)
	store := &Store{db: db}
	if err := store.init(); err != nil {
		_ = db.Close()
		return nil, err
	}
	return store, nil
}

func (s *Store) Close() error { return s.db.Close() }

func (s *Store) init() error {
	_, err := s.db.Exec(`
PRAGMA journal_mode=WAL;
PRAGMA busy_timeout=5000;
CREATE TABLE IF NOT EXISTS jobs (
  id TEXT PRIMARY KEY,
  project TEXT NOT NULL,
  repository TEXT NOT NULL,
  sha TEXT NOT NULL,
  suite TEXT NOT NULL,
  affinity TEXT NOT NULL,
  status TEXT NOT NULL,
  slot_id TEXT NOT NULL DEFAULT '',
  created_at INTEGER NOT NULL,
  started_at INTEGER NOT NULL DEFAULT 0,
  finished_at INTEGER NOT NULL DEFAULT 0,
  result_json TEXT NOT NULL DEFAULT '',
  error TEXT NOT NULL DEFAULT '',
  log_path TEXT NOT NULL DEFAULT '',
  tool_version TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS jobs_queue_idx ON jobs(status, project, created_at);
CREATE INDEX IF NOT EXISTS jobs_affinity_idx ON jobs(status, project, affinity, created_at);
CREATE TABLE IF NOT EXISTS affinity_leases (
  project TEXT NOT NULL,
  affinity TEXT NOT NULL,
  slot_id TEXT NOT NULL,
  expires_at INTEGER NOT NULL,
  PRIMARY KEY(project, affinity)
);
`)
	if err != nil {
		return fmt.Errorf("initialize slot database: %w", err)
	}
	return nil
}

func (s *Store) RecoverInterrupted(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx, `
UPDATE jobs
SET status = ?, slot_id = '', started_at = 0, error = 'agent restarted before completion'
WHERE status = ?`, JobQueued, JobRunning)
	if err != nil {
		return fmt.Errorf("recover interrupted validation jobs: %w", err)
	}
	return nil
}

func (s *Store) Enqueue(ctx context.Context, job Job) error {
	_, err := s.db.ExecContext(ctx, `
INSERT INTO jobs(id, project, repository, sha, suite, affinity, status, created_at, tool_version)
VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		job.ID, job.Project, job.Repository, job.SHA, job.Suite, job.Affinity,
		JobQueued, job.CreatedAt.UnixMilli(), job.ToolVersion)
	if err != nil {
		return fmt.Errorf("enqueue validation job: %w", err)
	}
	return nil
}

func (s *Store) Get(ctx context.Context, id string) (*Job, error) {
	row := s.db.QueryRowContext(ctx, `
SELECT id, project, repository, sha, suite, affinity, status, slot_id,
       created_at, started_at, finished_at, result_json, error, log_path, tool_version
FROM jobs WHERE id = ?`, id)
	job, err := scanJob(row)
	if err == sql.ErrNoRows {
		return nil, fmt.Errorf("validation job not found: %s", id)
	}
	if err != nil {
		return nil, err
	}
	return job, nil
}

func (s *Store) List(ctx context.Context, limit int) ([]Job, error) {
	if limit <= 0 || limit > 100 {
		limit = 20
	}
	rows, err := s.db.QueryContext(ctx, `
SELECT id, project, repository, sha, suite, affinity, status, slot_id,
       created_at, started_at, finished_at, result_json, error, log_path, tool_version
FROM jobs ORDER BY created_at DESC LIMIT ?`, limit)
	if err != nil {
		return nil, fmt.Errorf("list validation jobs: %w", err)
	}
	defer func() { _ = rows.Close() }()
	jobs := make([]Job, 0)
	for rows.Next() {
		job, scanErr := scanJob(rows)
		if scanErr != nil {
			return nil, scanErr
		}
		jobs = append(jobs, *job)
	}
	return jobs, rows.Err()
}

func (s *Store) Claim(ctx context.Context, project, slotID, affinity string, affinityOnly bool) (*Job, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback() }()

	now := time.Now().UTC().UnixMilli()
	query := `
SELECT jobs.id
FROM jobs
LEFT JOIN affinity_leases
  ON affinity_leases.project = jobs.project
 AND affinity_leases.affinity = jobs.affinity
 AND affinity_leases.expires_at > ?
WHERE jobs.status = ? AND jobs.project = ?
  AND (affinity_leases.slot_id IS NULL OR affinity_leases.slot_id = ?)`
	args := []any{now, JobQueued, project, slotID}
	if affinityOnly {
		query += ` AND jobs.affinity = ?`
		args = append(args, affinity)
	}
	query += ` ORDER BY jobs.created_at ASC LIMIT 1`
	var id string
	if err := tx.QueryRowContext(ctx, query, args...).Scan(&id); err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	result, err := tx.ExecContext(ctx, `
UPDATE jobs SET status = ?, slot_id = ?, started_at = ?
WHERE id = ? AND status = ?`, JobRunning, slotID, now, id, JobQueued)
	if err != nil {
		return nil, err
	}
	count, err := result.RowsAffected()
	if err != nil || count != 1 {
		return nil, err
	}
	if err := tx.Commit(); err != nil {
		return nil, err
	}
	return s.Get(ctx, id)
}

func (s *Store) SetAffinityLease(ctx context.Context, project, affinity, slotID string, expiresAt time.Time) error {
	_, err := s.db.ExecContext(ctx, `
INSERT INTO affinity_leases(project, affinity, slot_id, expires_at)
VALUES(?, ?, ?, ?)
ON CONFLICT(project, affinity) DO UPDATE SET slot_id = excluded.slot_id, expires_at = excluded.expires_at`,
		project, affinity, slotID, expiresAt.UTC().UnixMilli())
	if err != nil {
		return fmt.Errorf("set affinity lease: %w", err)
	}
	return nil
}

func (s *Store) ClearAffinityLease(ctx context.Context, project, affinity, slotID string) error {
	_, err := s.db.ExecContext(ctx, `
DELETE FROM affinity_leases WHERE project = ? AND affinity = ? AND slot_id = ?`, project, affinity, slotID)
	if err != nil {
		return fmt.Errorf("clear affinity lease: %w", err)
	}
	return nil
}

func (s *Store) Complete(ctx context.Context, id, status string, result *Result, runErr error, logPath string) error {
	resultJSON := ""
	if result != nil {
		data, err := json.Marshal(result)
		if err != nil {
			return err
		}
		resultJSON = string(data)
	}
	errorText := ""
	if runErr != nil {
		errorText = runErr.Error()
	}
	_, err := s.db.ExecContext(ctx, `
UPDATE jobs SET status = ?, finished_at = ?, result_json = ?, error = ?, log_path = ?
WHERE id = ?`, status, time.Now().UTC().UnixMilli(), resultJSON, errorText, logPath, id)
	if err != nil {
		return fmt.Errorf("complete validation job: %w", err)
	}
	return nil
}

type rowScanner interface {
	Scan(dest ...any) error
}

func scanJob(row rowScanner) (*Job, error) {
	var (
		job                        Job
		created, started, finished int64
		resultJSON                 string
	)
	if err := row.Scan(
		&job.ID, &job.Project, &job.Repository, &job.SHA, &job.Suite, &job.Affinity,
		&job.Status, &job.SlotID, &created, &started, &finished, &resultJSON,
		&job.Error, &job.LogPath, &job.ToolVersion,
	); err != nil {
		return nil, err
	}
	job.CreatedAt = time.UnixMilli(created).UTC()
	if started > 0 {
		job.StartedAt = time.UnixMilli(started).UTC()
	}
	if finished > 0 {
		job.FinishedAt = time.UnixMilli(finished).UTC()
	}
	if resultJSON != "" {
		var result Result
		if err := json.Unmarshal([]byte(resultJSON), &result); err != nil {
			return nil, fmt.Errorf("decode validation result for %s: %w", job.ID, err)
		}
		job.Result = &result
	}
	return &job, nil
}
