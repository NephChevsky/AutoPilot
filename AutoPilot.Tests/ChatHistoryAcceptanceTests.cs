using AutoPilot.Services;
using GitHub.Copilot;

namespace AutoPilot.Tests;

public class ChatHistoryAcceptanceTests
{
    private static ChatTranscript New() => new() { OrchestratorName = "Boss" };

    private static UserMessageEvent User(string text, string? agentId = null) =>
        new() { AgentId = agentId, Data = new UserMessageData { Content = text } };

    private static AssistantMessageEvent Asst(string id, string text, string? agentId = null) =>
        new() { AgentId = agentId, Data = new AssistantMessageData { MessageId = id, Content = text } };

    private static AssistantMessageDeltaEvent Delta(string id, string text, string? agentId = null) =>
        new() { AgentId = agentId, Data = new AssistantMessageDeltaData { MessageId = id, DeltaContent = text } };

    private static ToolExecutionStartEvent ToolStart(string id, string name, string? agentId = null) =>
        new() { AgentId = agentId, Data = new ToolExecutionStartData { ToolCallId = id, ToolName = name } };

    private static ToolExecutionCompleteEvent ToolDone(string id, bool ok, string? agentId = null) =>
        new() { AgentId = agentId, Data = new ToolExecutionCompleteData { ToolCallId = id, Success = ok } };

    private static SubagentStartedEvent SubStart(string? envAgent, string name, string? display) =>
        new() { AgentId = envAgent, Data = new SubagentStartedData { AgentName = name, AgentDisplayName = display!, AgentDescription = "", ToolCallId = "tc-sub" } };

    private static SubagentCompletedEvent SubDone(string name, string? display) =>
        new() { Data = new SubagentCompletedData { AgentName = name, AgentDisplayName = display!, ToolCallId = "tc-sub" } };

    private static SubagentFailedEvent SubFail(string name, string? display, string error) =>
        new() { Data = new SubagentFailedData { AgentName = name, AgentDisplayName = display!, Error = error, ToolCallId = "tc-sub" } };

    private static SessionErrorEvent Err(string msg) =>
        new() { Data = new SessionErrorData { Message = msg, ErrorType = "error" } };

    // AC-7 / AC-2
    [Fact]
    public void Constants_AndDefaults()
    {
        Assert.Equal("task", ChatTranscript.TaskToolName);
        Assert.Equal("Sub-agent", ChatTranscript.UnknownSubagentName);
        Assert.Empty(new ChatTranscript().GetEntries());
    }

    [Fact]
    public void Replay_RootUserMessage_BecomesUserEntryAuthoredYou()
    {
        var t = New();
        t.Replay([User("hello")]);
        var e = Assert.Single(t.GetEntries());
        Assert.Equal(ChatEntryKind.User, e.Kind);
        Assert.Equal("You", e.Author);
        Assert.Equal("hello", e.Text);
    }

    [Fact]
    public void Live_UserMessage_NotAdded()
    {
        var t = New();
        Assert.False(t.Apply(User("hi")));
        Assert.Empty(t.GetEntries());
    }

    [Fact]
    public void Replay_SubagentInternalUserMessage_Skipped()
    {
        var t = New();
        t.Replay([User("internal prompt", "sub1")]);
        Assert.Empty(t.GetEntries());
    }

    [Fact]
    public void Deltas_AppendToSingleAssistantEntry_ByMessageId()
    {
        var t = New();
        Assert.True(t.Apply(Delta("m1", "Hel")));
        Assert.True(t.Apply(Delta("m1", "lo")));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal(ChatEntryKind.Assistant, e.Kind);
        Assert.Equal("Boss", e.Author);
        Assert.False(e.IsSubagent);
        Assert.Equal("m1", e.Key);
        Assert.Equal("Hello", e.Text);
    }

    [Fact]
    public void DifferentMessageIds_SeparateEntries()
    {
        var t = New();
        t.Apply(Delta("m1", "a"));
        t.Apply(Delta("m2", "b"));
        Assert.Equal(2, t.GetEntries().Count);
    }

    [Fact]
    public void FinalMessage_SetsContent_OverridingDeltas()
    {
        var t = New();
        t.Apply(Delta("m1", "par"));
        t.Apply(Asst("m1", "full text"));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal("full text", e.Text);
    }

    [Fact]
    public void FinalMessage_WithoutDeltas_CreatesEntry()
    {
        var t = New();
        t.Apply(Asst("m1", "only final"));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal("only final", e.Text);
        Assert.Equal(ChatEntryKind.Assistant, e.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void FinalMessage_EmptyContent_CreatesNoEntry(string? content)
    {
        var t = New();
        t.Apply(Asst("m1", content!));
        Assert.Empty(t.GetEntries());
    }

    [Fact]
    public void OrchestratorName_ChangeIsUsedForAuthor()
    {
        var t = New();
        t.OrchestratorName = "Other";
        t.Apply(Asst("m1", "x"));
        Assert.Equal("Other", t.GetEntries()[0].Author);
    }

    [Fact]
    public void SubagentMessage_RegisteredName_And_Unknown()
    {
        var t = New();
        t.RegisterSubagent("s1", "Coder");
        t.Apply(Asst("m1", "from coder", "s1"));
        t.Apply(Asst("m2", "from ???", "zzz"));
        var es = t.GetEntries();
        Assert.Equal("Coder", es[0].Author);
        Assert.True(es[0].IsSubagent);
        Assert.Equal("Sub-agent", es[1].Author);
        Assert.True(es[1].IsSubagent);
    }

    [Fact]
    public void ToolStart_AddsRunningEntry_CompleteSetsSucceeded()
    {
        var t = New();
        Assert.True(t.Apply(ToolStart("c1", "read_file")));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal(ChatEntryKind.Tool, e.Kind);
        Assert.Equal("read_file", e.Text);
        Assert.Equal("c1", e.Key);
        Assert.Null(e.Succeeded);
        Assert.True(t.Apply(ToolDone("c1", true)));
        Assert.True(e.Succeeded);
        Assert.Single(t.GetEntries());
    }

    [Fact]
    public void ToolComplete_Failure_SetsSucceededFalse()
    {
        var t = New();
        t.Apply(ToolStart("c1", "x"));
        t.Apply(ToolDone("c1", false));
        Assert.False(t.GetEntries()[0].Succeeded);
    }

    [Fact]
    public void TaskTool_Start_AddsNoEntry()
    {
        var t = New();
        t.Apply(ToolStart("c1", "task"));
        Assert.Empty(t.GetEntries());
    }

    [Fact]
    public void ToolFromSubagent_IsMarkedSubagent()
    {
        var t = New();
        t.Apply(ToolStart("c1", "grep", "s1"));
        Assert.True(t.GetEntries()[0].IsSubagent);
    }

    [Fact]
    public void SubagentStarted_AddsDelegationActivity_PrefersDisplayName()
    {
        var t = New();
        Assert.True(t.Apply(SubStart("s1", "coder", "Code Wizard")));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal(ChatEntryKind.Activity, e.Kind);
        Assert.Equal("Boss delegated to Code Wizard", e.Text);
    }

    [Fact]
    public void SubagentStarted_FallsBackToName()
    {
        var t = New();
        t.Apply(SubStart("s1", "coder", null));
        Assert.Equal("Boss delegated to coder", t.GetEntries()[0].Text);
    }

    [Fact]
    public void SubagentStarted_EnvelopeAgentId_MapsLaterEventsToName()
    {
        var t = New();
        t.Apply(SubStart("s1", "coder", "Code Wizard"));
        t.Apply(Asst("m1", "hi", "s1"));
        Assert.Equal("Code Wizard", t.GetEntries()[^1].Author);
    }

    [Fact]
    public void SubagentCompleted_AddsFinishedActivity()
    {
        var t = New();
        t.Apply(SubDone("coder", "Code Wizard"));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal(ChatEntryKind.Activity, e.Kind);
        Assert.Equal("Code Wizard finished", e.Text);
    }

    [Fact]
    public void SubagentFailed_AddsErrorEntry()
    {
        var t = New();
        t.Apply(SubFail("coder", "Code Wizard", "boom"));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal(ChatEntryKind.Error, e.Kind);
        Assert.Equal("Code Wizard failed: boom", e.Text);
    }

    [Fact]
    public void SubagentCompletedAndFailed_AfterStarted_WithEnvelopeAgentId_UseDisplayName()
    {
        var t = New();
        t.Apply(SubStart("s1", "coder", "Code Wizard"));
        t.Apply(new SubagentCompletedEvent { AgentId = "s1", Data = new SubagentCompletedData { AgentName = "coder", AgentDisplayName = "Code Wizard", ToolCallId = "x" } });
        t.Apply(new SubagentFailedEvent { AgentId = "s1", Data = new SubagentFailedData { AgentName = "coder", AgentDisplayName = "Code Wizard", Error = "boom", ToolCallId = "x" } });
        var es = t.GetEntries();
        Assert.Equal("Code Wizard finished", es[1].Text);
        Assert.Equal("Code Wizard failed: boom", es[2].Text);
    }

    [Fact]
    public void SessionError_AddsErrorEntry()
    {
        var t = New();
        Assert.True(t.Apply(Err("kaput")));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal(ChatEntryKind.Error, e.Kind);
        Assert.Contains("kaput", e.Text);
    }

    [Fact]
    public void Add_And_Clear()
    {
        var t = New();
        t.Add(new ChatEntry { Kind = ChatEntryKind.User, Author = "You", Text = "x" });
        Assert.Single(t.GetEntries());
        t.Clear();
        Assert.Empty(t.GetEntries());
    }

    [Fact]
    public void GetEntries_IsSnapshot()
    {
        var t = New();
        t.Add(new ChatEntry { Kind = ChatEntryKind.User, Author = "You", Text = "x" });
        var snap = t.GetEntries();
        t.Add(new ChatEntry { Kind = ChatEntryKind.User, Author = "You", Text = "y" });
        Assert.Single(snap);
    }

    [Fact]
    public void Replay_Empty_NoEntries_AndSecondReplayAfterClear()
    {
        var t = New();
        t.Replay([]);
        Assert.Empty(t.GetEntries());
    }

    private static List<SessionEvent> FullSequence() =>
    [
        User("do it"),
        Delta("m1", "Plan"),
        Asst("m1", "Plan: delegate"),
        ToolStart("tc0", "task"),
        SubStart("s1", "coder", "Code Wizard"),
        User("internal", "s1"),
        ToolStart("tc1", "write_file", "s1"),
        ToolDone("tc1", true, "s1"),
        Asst("m2", "wrote it", "s1"),
        SubDone("coder", "Code Wizard"),
        ToolStart("tc2", "bad"),
        ToolDone("tc2", false),
        SubFail("other", null, "nope"),
        Err("session broke"),
        Asst("m3", "done"),
    ];

    private static List<(ChatEntryKind, string, string, string?, bool, bool?)> Shape(ChatTranscript t) =>
        t.GetEntries().Where(e => e.Kind != ChatEntryKind.User)
            .Select(e => (e.Kind, e.Author, e.Text, e.Key, e.IsSubagent, e.Succeeded)).ToList();

    [Fact]
    public void Replay_ProducesSameEntries_AsLive_ExceptUserMessages()
    {
        var live = New();
        foreach (var e in FullSequence()) live.Apply(e);
        var replay = New();
        replay.Replay(FullSequence());

        Assert.Equal(Shape(live), Shape(replay));
        Assert.Contains(replay.GetEntries(), e => e.Kind == ChatEntryKind.User && e.Text == "do it" && e.Author == "You");
        Assert.DoesNotContain(replay.GetEntries(), e => e.Text == "internal");
        Assert.Equal("Code Wizard", replay.GetEntries().First(e => e.Key == "m2").Author);
    }

    [Fact]
    public void Replay_StreamingDeltasOnly_EqualsLive()
    {
        var evs = new List<SessionEvent> { Delta("m1", "a"), Delta("m1", "b"), Asst("m1", "ab") };
        var live = New(); foreach (var e in evs) live.Apply(e);
        var rep = New(); rep.Replay(evs);
        Assert.Equal(Shape(live), Shape(rep));
    }
}
