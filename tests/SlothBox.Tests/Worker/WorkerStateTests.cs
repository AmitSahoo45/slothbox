using SlothBox.Server.Api;
using SlothBox.Server.Worker;

namespace SlothBox.Tests.Worker;

public sealed class WorkerStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Pause_and_resume_change_the_state_and_announce_it()
    {
        var state = new WorkerState();
        var announced = 0;
        state.Changed += () => announced++;

        state.Pause(PauseReason.DriveFull);
        (state.Paused, state.Reason).ShouldBe((true, (PauseReason?)PauseReason.DriveFull));

        state.Pause(PauseReason.User);
        (state.Paused, state.Reason).ShouldBe((true, (PauseReason?)PauseReason.User));

        state.Resume();
        (state.Paused, state.Reason).ShouldBe((false, (PauseReason?)null));
        state.Resume();
        announced.ShouldBe(3);
    }

    [Fact]
    public void A_users_pause_outranks_the_workers_own_pauses()
    {
        var state = new WorkerState();
        state.Pause(PauseReason.GemmaOffline);
        state.Pause(PauseReason.User);

        state.Pause(PauseReason.GemmaOffline);
        state.Reason.ShouldBe(PauseReason.User);
        state.Pause(PauseReason.DriveFull);
        state.Reason.ShouldBe(PauseReason.User);
    }

    [Fact]
    public void A_full_drive_outranks_an_offline_Gemma()
    {
        var state = new WorkerState();
        state.Pause(PauseReason.GemmaOffline);
        state.Pause(PauseReason.DriveFull);

        state.Pause(PauseReason.GemmaOffline);

        state.Reason.ShouldBe(PauseReason.DriveFull);
    }

    [Fact]
    public void The_worker_can_lift_only_its_own_pause_reason()
    {
        var state = new WorkerState();
        state.Pause(PauseReason.User);

        state.TryResume(PauseReason.GemmaOffline).ShouldBeFalse();
        (state.Paused, state.Reason).ShouldBe((true, (PauseReason?)PauseReason.User));

        state.TryResume(PauseReason.User).ShouldBeTrue();
        state.Paused.ShouldBeFalse();
        state.TryResume(PauseReason.GemmaOffline).ShouldBeTrue();
    }

    [Fact]
    public async Task Waiting_ends_when_the_worker_is_resumed()
    {
        var state = new WorkerState();
        state.Pause(PauseReason.User);
        var waiting = state.WaitForResumeAsync(null, Ct);
        waiting.IsCompleted.ShouldBeFalse();

        state.Resume();

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Waiting_with_a_timeout_ends_on_its_own_still_paused()
    {
        var state = new WorkerState();
        state.Pause(PauseReason.GemmaOffline);

        (await state.WaitForResumeAsync(TimeSpan.FromMilliseconds(20), Ct)).ShouldBeFalse();

        state.Paused.ShouldBeTrue();
    }

    [Fact]
    public async Task Waiting_while_not_paused_returns_at_once()
    {
        (await new WorkerState().WaitForResumeAsync(null, Ct)).ShouldBeTrue();
    }
}
