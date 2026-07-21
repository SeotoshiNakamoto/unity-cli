package slot

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

type Agent struct {
	Config      *AgentConfig
	Store       *Store
	ToolVersion string
	Runner      Runner
	waitGroup   sync.WaitGroup
}

func NewAgent(config *AgentConfig, toolVersion string) (*Agent, error) {
	if err := os.MkdirAll(config.LogDir, 0o755); err != nil {
		return nil, fmt.Errorf("create validation log directory: %w", err)
	}
	store, err := OpenStore(config.StatePath)
	if err != nil {
		return nil, err
	}
	return &Agent{
		Config:      config,
		Store:       store,
		ToolVersion: toolVersion,
		Runner:      Runner{LogDir: config.LogDir, ToolVersion: toolVersion},
	}, nil
}

func (a *Agent) Close() error {
	return a.Store.Close()
}

func (a *Agent) Serve(ctx context.Context) error {
	listener, err := listenEndpoint(a.Config.Endpoint)
	if err != nil {
		return fmt.Errorf("listen on %s: %w", a.Config.Endpoint, err)
	}
	defer func() { _ = listener.Close() }()
	if err := a.Store.RecoverInterrupted(ctx); err != nil {
		return err
	}
	go func() {
		<-ctx.Done()
		_ = listener.Close()
	}()

	for _, slotConfig := range a.Config.Slots {
		slotCopy := slotConfig
		a.waitGroup.Add(1)
		go func() {
			defer a.waitGroup.Done()
			a.runSlot(ctx, slotCopy)
		}()
	}

	for {
		conn, acceptErr := listener.Accept()
		if acceptErr != nil {
			if ctx.Err() != nil {
				break
			}
			return fmt.Errorf("accept slot request: %w", acceptErr)
		}
		go a.handleConnection(ctx, conn)
	}
	a.waitGroup.Wait()
	return nil
}

func (a *Agent) handleConnection(ctx context.Context, conn net.Conn) {
	defer func() { _ = conn.Close() }()
	var request Request
	if err := json.NewDecoder(conn).Decode(&request); err != nil {
		_ = json.NewEncoder(conn).Encode(Response{OK: false, Error: err.Error()})
		return
	}
	response := a.handleRequest(ctx, request)
	_ = json.NewEncoder(conn).Encode(response)
}

func (a *Agent) handleRequest(ctx context.Context, request Request) Response {
	if request.Version != ProtocolVersion {
		return Response{OK: false, Error: fmt.Sprintf("unsupported protocol version %d", request.Version)}
	}
	switch request.Action {
	case "submit":
		job, err := a.submit(ctx, request.Submit)
		if err != nil {
			return Response{OK: false, Error: err.Error()}
		}
		return Response{OK: true, Job: job}
	case "status", "result":
		if request.JobID == "" {
			jobs, err := a.Store.List(ctx, 20)
			if err != nil {
				return Response{OK: false, Error: err.Error()}
			}
			return Response{OK: true, Jobs: jobs}
		}
		job, err := a.Store.Get(ctx, request.JobID)
		if err != nil {
			return Response{OK: false, Error: err.Error()}
		}
		return Response{OK: true, Job: job}
	case "doctor":
		report := a.Doctor(ctx)
		return Response{OK: true, Doctor: &report}
	default:
		return Response{OK: false, Error: fmt.Sprintf("unknown slot action %q", request.Action)}
	}
}

func (a *Agent) submit(ctx context.Context, request *SubmitRequest) (*Job, error) {
	if request == nil {
		return nil, fmt.Errorf("missing submit request")
	}
	repository, err := resolveRepository(ctx, request.Repository)
	if err != nil {
		return nil, err
	}
	sha, err := resolveCommit(ctx, repository, request.SHA)
	if err != nil {
		return nil, err
	}
	projectConfig, err := projectConfigAtCommit(ctx, repository, sha)
	if err != nil {
		return nil, err
	}
	suite := request.Suite
	if suite == "" {
		suite = "compile"
	}
	if _, ok := projectConfig.Suites[suite]; !ok {
		return nil, fmt.Errorf("suite %q is not defined for %s", suite, projectConfig.Project)
	}
	sourceCommon, err := gitCommonDir(ctx, repository)
	if err != nil {
		return nil, err
	}
	hasCompatibleSlot := false
	for _, configuredSlot := range a.Config.Slots {
		if configuredSlot.Project != projectConfig.Project {
			continue
		}
		slotCommon, commonErr := gitCommonDir(ctx, configuredSlot.Worktree)
		if commonErr == nil && samePath(sourceCommon, slotCommon) {
			hasCompatibleSlot = true
			break
		}
	}
	if !hasCompatibleSlot {
		return nil, fmt.Errorf("no validation slot for %s shares this repository's Git object store", projectConfig.Project)
	}
	id, err := newJobID()
	if err != nil {
		return nil, err
	}
	affinity := strings.TrimSpace(request.Affinity)
	if affinity == "" {
		affinity = repository
	}
	job := Job{
		ID:          id,
		Project:     projectConfig.Project,
		Repository:  repository,
		SHA:         sha,
		Suite:       suite,
		Affinity:    affinity,
		Status:      JobQueued,
		CreatedAt:   time.Now().UTC(),
		ToolVersion: a.ToolVersion,
	}
	if err := a.Store.Enqueue(ctx, job); err != nil {
		return nil, err
	}
	return a.Store.Get(ctx, id)
}

func (a *Agent) runSlot(ctx context.Context, configuredSlot SlotConfig) {
	var stickyAffinity string
	var stickyUntil time.Time
	for ctx.Err() == nil {
		if stickyAffinity != "" && !time.Now().Before(stickyUntil) {
			_ = a.Store.ClearAffinityLease(context.Background(), configuredSlot.Project, stickyAffinity, configuredSlot.ID)
			stickyAffinity = ""
			stickyUntil = time.Time{}
		}
		affinityOnly := stickyAffinity != "" && time.Now().Before(stickyUntil)
		job, err := a.Store.Claim(ctx, configuredSlot.Project, configuredSlot.ID, stickyAffinity, affinityOnly)
		if err != nil || job == nil {
			select {
			case <-ctx.Done():
				return
			case <-time.After(a.Config.PollInterval()):
			}
			continue
		}
		result, logPath, runErr := a.Runner.Run(ctx, *job, configuredSlot)
		status := JobPassed
		if runErr != nil {
			status = JobFailed
			stickyAffinity = job.Affinity
			stickyUntil = time.Now().Add(a.Config.StickyFailureDuration())
			_ = a.Store.SetAffinityLease(context.Background(), job.Project, job.Affinity, configuredSlot.ID, stickyUntil)
		} else {
			_ = a.Store.ClearAffinityLease(context.Background(), job.Project, job.Affinity, configuredSlot.ID)
			stickyAffinity = ""
			stickyUntil = time.Time{}
		}
		_ = a.Store.Complete(context.Background(), job.ID, status, result, runErr, logPath)
	}
}

func (a *Agent) Doctor(ctx context.Context) DoctorReport {
	report := DoctorReport{
		ConfigPath: a.Config.ConfigPath(),
		StatePath:  a.Config.StatePath,
		Endpoint:   a.Config.Endpoint,
		Healthy:    true,
		Slots:      make([]SlotDoctor, 0, len(a.Config.Slots)),
	}
	for _, configuredSlot := range a.Config.Slots {
		slotReport := SlotDoctor{
			ID:              configuredSlot.ID,
			Project:         configuredSlot.Project,
			Worktree:        configuredSlot.Worktree,
			UnityProject:    filepath.Join(configuredSlot.Worktree, configuredSlot.ProjectSubdir),
			UnityCLI:        configuredSlot.UnityCLI,
			UnityExecutable: configuredSlot.UnityExecutable,
			DesktopIndex:    configuredSlot.DesktopIndex,
			Healthy:         true,
		}
		common, err := gitCommonDir(ctx, configuredSlot.Worktree)
		if err != nil {
			slotReport.Healthy = false
			slotReport.Error = err.Error()
		} else {
			slotReport.GitCommonDir = common
		}
		if _, err := os.Stat(filepath.Join(slotReport.UnityProject, "ProjectSettings", "ProjectVersion.txt")); err != nil {
			slotReport.Healthy = false
			slotReport.Error = joinError(slotReport.Error, "Unity project missing")
		}
		if configuredSlot.UnityCLI != "" {
			if _, err := os.Stat(configuredSlot.UnityCLI); err != nil {
				slotReport.Healthy = false
				slotReport.Error = joinError(slotReport.Error, "unity-cli executable missing")
			}
		}
		if configuredSlot.UnityExecutable != "" {
			if _, err := os.Stat(configuredSlot.UnityExecutable); err != nil {
				slotReport.Healthy = false
				slotReport.Error = joinError(slotReport.Error, "Unity executable missing")
			}
		}
		if configuredSlot.DesktopIndex != nil {
			if _, err := os.Stat(configuredSlot.DesktopHelper); err != nil {
				slotReport.Healthy = false
				slotReport.Error = joinError(slotReport.Error, "virtual desktop helper missing")
			} else {
				desktopCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
				desktopErr := doctorDesktopHelper(desktopCtx, configuredSlot.DesktopHelper, *configuredSlot.DesktopIndex)
				cancel()
				if desktopErr != nil {
					slotReport.Healthy = false
					slotReport.Error = joinError(slotReport.Error, desktopErr.Error())
				}
			}
		}
		if !slotReport.Healthy {
			report.Healthy = false
		}
		report.Slots = append(report.Slots, slotReport)
	}
	return report
}

func joinError(existing, next string) string {
	if existing == "" {
		return next
	}
	return existing + "; " + next
}
