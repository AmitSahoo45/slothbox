using SlothBox.Core;
using SlothBox.Core.Folders;
using SlothBox.Core.Gate;
using SlothBox.Core.Pipeline;

namespace SlothBox.Server.Watching;

// What the watching side learns when the worker is done with a job: a file that stayed where it was, unchanged,
// becomes a finished path (spec 4.5), and a tidied file counts towards its folder's progress.
public sealed class FolderJobs(IFinishedPaths finished, TidyBatches tidy)
{
    // seen: the file's stamp when the job was claimed. Called before the job is marked done, so a sweep cannot queue
    // the path again in between.
    public async Task RecordFinishedAsync(Job job, PipelineResult result, FileStamp? seen, CancellationToken ct)
    {
        var file = job.File;
        // A filing that left the file in place (copy-only), or a file skipped for its ignored hash (an undone file).
        // The sweep tries locked files and failures again; it never queues temporary, hidden, system or baseline
        // files, and a skip for the folder's sake (switched off, removed) says nothing about the file.
        if (seen is not { } stamp ||
            result is not (PipelineResult.Filed or PipelineResult.Skipped { Reason: GateSkipReason.Ignored }))
            return;
        // A folder job whose folder was removed meanwhile has no folder to be finished in.
        if (file.Origin == FileOrigin.Folder && file.WatchedFolderId is null)
            return;
        // Moved away, or changed while the job ran: nothing to remember, or the sweep must look again.
        if (FileStamp.Of(file.Path) != stamp)
            return;
        await finished.RecordAsync(new FinishedPath(file.Path, file.WatchedFolderId, stamp), ct);
    }

    // The job will not run again: a tidied file counts towards its folder's progress.
    public void JobOver(Job job) => tidy.Over(job.File.WatchedFolderId, job.File.Path);
}
