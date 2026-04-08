using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEditor;

namespace UnityCliConnector
{
    /// <summary>
    /// Manages async job execution for long-running commands.
    /// Jobs are queued through the normal WorkItem pipeline but return
    /// a job_id immediately. Results are cached until polled via job_status.
    /// </summary>
    [InitializeOnLoad]
    public static class AsyncJobManager
    {
        struct JobEntry
        {
            public string Id;
            public string Command;
            public string Status; // "running", "completed", "failed"
            public object Result;
            public DateTime CreatedAt;
        }

        static readonly ConcurrentDictionary<string, JobEntry> s_Jobs = new();
        static int s_Counter;
        static double s_LastCleanup;

        static AsyncJobManager()
        {
            EditorApplication.update += Cleanup;
        }

        public static string CreateJob(string command, TaskCompletionSource<object> tcs)
        {
            var id = $"{command}_{DateTime.Now:HHmmss}_{++s_Counter}";
            s_Jobs[id] = new JobEntry
            {
                Id = id,
                Command = command,
                Status = "running",
                CreatedAt = DateTime.Now,
            };

            tcs.Task.ContinueWith(t =>
            {
                if (!s_Jobs.TryGetValue(id, out var entry))
                    return;

                entry.Status = t.IsFaulted ? "failed" : "completed";
                entry.Result = t.IsFaulted
                    ? new ErrorResponse(t.Exception?.InnerException?.Message ?? "Unknown error")
                    : t.Result;
                s_Jobs[id] = entry;
            });

            return id;
        }

        public static object GetJobStatus(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                return new ErrorResponse("job_id is required.");

            if (!s_Jobs.TryGetValue(jobId, out var entry))
                return new ErrorResponse($"Unknown job: {jobId}");

            if (entry.Status == "running")
                return new SuccessResponse("running", new { job_id = jobId, status = "running" });

            s_Jobs.TryRemove(jobId, out _);
            return entry.Result;
        }

        static void Cleanup()
        {
            // Run every 60 seconds
            if (EditorApplication.timeSinceStartup - s_LastCleanup < 60)
                return;
            s_LastCleanup = EditorApplication.timeSinceStartup;

            var cutoff = DateTime.Now.AddMinutes(-30);
            foreach (var kv in s_Jobs)
            {
                if (kv.Value.CreatedAt < cutoff)
                    s_Jobs.TryRemove(kv.Key, out _);
            }
        }
    }
}
