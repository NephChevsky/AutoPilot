using AutoPilot.Services;
using GitHub.Copilot;

namespace AutoPilot.Tests;

public class ReasoningIntentAcceptanceTests
{
    private static ChatTranscript New() => new() { OrchestratorName = "Boss" };

    private static AssistantReasoningDeltaEvent Delta(string id, string text, string? agent = null) =>
        new() { AgentId = agent, Data = new AssistantReasoningDeltaData { ReasoningId = id, DeltaContent = text } };

    private static AssistantReasoningEvent Full(string id, string text, string? agent = null) =>
        new() { AgentId = agent, Data = new AssistantReasoningData { ReasoningId = id, Content = text } };

    private static AssistantIntentEvent Intent(string text, string? agent = null) =>
        new() { AgentId = agent, Data = new AssistantIntentData { Intent = text } };

    private static AssistantMessageEvent Msg(string id, string text) =>
        new() { Data = new AssistantMessageData { MessageId = id, Content = text } };

    private static ChatTranscript WithSub()
    {
        var t = New();
        t.RegisterSubagent("sa1", "Coder");
        return t;
    }

    // AC-1
    [Fact]
    public void AC1_Delta_CreatesThenAppends()
    {
        var t = New();
        Assert.True(t.Apply(Delta("r1", "Hel")));
        Assert.True(t.Apply(Delta("r1", "lo")));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal(ChatEntryKind.Reasoning, e.Kind);
        Assert.Equal("r1", e.Key);
        Assert.Equal("Boss", e.Author);
        Assert.False(e.IsSubagent);
        Assert.Equal("Hello", e.Text);
    }

    [Fact]
    public void AC1_Delta_SubagentAuthor()
    {
        var t = WithSub();
        t.Apply(Delta("r1", "x", "sa1"));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal("Coder", e.Author);
        Assert.True(e.IsSubagent);
    }

    [Fact]
    public void AC1_Delta_UnknownSubagent()
    {
        var t = New();
        t.Apply(Delta("r1", "x", "zzz"));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal("Sub-agent", e.Author);
        Assert.True(e.IsSubagent);
    }

    [Fact]
    public void AC1_Delta_LearnsNameFromSubagentStarted()
    {
        var t = New();
        t.Apply(new SubagentStartedEvent
        {
            AgentId = "sa9",
            Data = new SubagentStartedData { AgentName = "n", AgentDisplayName = "Disp", AgentDescription = "d", ToolCallId = "tc" }
        });
        int before = t.GetEntries().Count;
        t.Apply(Delta("r1", "x", "sa9"));
        var e = t.GetEntries()[before];
        Assert.Equal(ChatEntryKind.Reasoning, e.Kind);
        Assert.NotEqual("Sub-agent", e.Author);
        Assert.True(e.IsSubagent);
    }

    [Fact]
    public void AC1_EmptyDelta_UnknownId_CreatesNothing_ReturnsFalse()
    {
        var t = New();
        Assert.False(t.Apply(Delta("r1", "")));
        Assert.Empty(t.GetEntries());
    }

    [Fact]
    public void AC1_EmptyDelta_KnownId_ReturnsFalse_NoChange()
    {
        var t = New();
        t.Apply(Delta("r1", "abc"));
        Assert.False(t.Apply(Delta("r1", "")));
        Assert.Equal("abc", Assert.Single(t.GetEntries()).Text);
    }

    [Fact]
    public void AC1_DifferentIds_SeparateEntries()
    {
        var t = New();
        t.Apply(Delta("r1", "a"));
        t.Apply(Delta("r2", "b"));
        var es = t.GetEntries();
        Assert.Equal(2, es.Count);
        Assert.Equal("a", es[0].Text);
        Assert.Equal("b", es[1].Text);
    }

    // AC-2
    [Fact]
    public void AC2_Full_ReplacesDeltaText()
    {
        var t = New();
        t.Apply(Delta("r1", "par"));
        Assert.True(t.Apply(Full("r1", "complete text")));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal("complete text", e.Text);
        Assert.Equal(ChatEntryKind.Reasoning, e.Kind);
    }

    [Fact]
    public void AC2_Full_CreatesWhenAbsent()
    {
        var t = WithSub();
        Assert.True(t.Apply(Full("r1", "whole", "sa1")));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal("whole", e.Text);
        Assert.Equal("r1", e.Key);
        Assert.Equal("Coder", e.Author);
        Assert.True(e.IsSubagent);
    }

    [Fact]
    public void AC2_EmptyFull_NoEntry()
    {
        var t = New();
        Assert.False(t.Apply(Full("r1", "")));
        Assert.Empty(t.GetEntries());
    }

    [Fact]
    public void AC2_DeltaAfterFull_Appends()
    {
        var t = New();
        t.Apply(Full("r1", "A"));
        t.Apply(Delta("r1", "B"));
        Assert.Equal("AB", Assert.Single(t.GetEntries()).Text);
    }

    // AC-3
    [Fact]
    public void AC3_Intent_Orchestrator()
    {
        var t = New();
        Assert.True(t.Apply(Intent("  Exploring code  ")));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal(ChatEntryKind.Intent, e.Kind);
        Assert.Equal("Boss", e.Author);
        Assert.False(e.IsSubagent);
        Assert.Equal("Exploring code", e.Text);
    }

    [Fact]
    public void AC3_Intent_Subagent()
    {
        var t = WithSub();
        t.Apply(Intent("Fixing bug", "sa1"));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal("Coder", e.Author);
        Assert.True(e.IsSubagent);
        Assert.Equal("Fixing bug", e.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void AC3_BlankIntent_Ignored(string s)
    {
        var t = New();
        Assert.False(t.Apply(Intent(s)));
        Assert.Empty(t.GetEntries());
    }

    [Fact]
    public void AC3_Duplicate_SameAgent_NotReAdded()
    {
        var t = New();
        Assert.True(t.Apply(Intent("Plan")));
        Assert.False(t.Apply(Intent("Plan")));
        Assert.Single(t.GetEntries());
    }

    [Fact]
    public void AC3_Duplicate_DifferentAgent_Added()
    {
        var t = WithSub();
        t.Apply(Intent("Plan"));
        Assert.True(t.Apply(Intent("Plan", "sa1")));
        Assert.Equal(2, t.GetEntries().Count);
    }

    [Fact]
    public void AC3_Duplicate_PerAgent_InterleavedStillSuppressed()
    {
        var t = WithSub();
        t.Apply(Intent("Plan"));
        t.Apply(Intent("Other", "sa1"));
        Assert.False(t.Apply(Intent("Plan")));
        Assert.Equal(2, t.GetEntries().Count);
    }

    [Fact]
    public void AC3_Same_AfterDifferentIntent_Added()
    {
        var t = New();
        t.Apply(Intent("A"));
        t.Apply(Intent("B"));
        Assert.True(t.Apply(Intent("A")));
        Assert.Equal(3, t.GetEntries().Count);
    }

    // AC-4 (transcript ordering)
    [Fact]
    public void AC4_ChronologicalOrder()
    {
        var t = New();
        t.Apply(Intent("I1"));
        t.Apply(Delta("r1", "think"));
        t.Apply(Msg("m1", "answer"));
        t.Apply(Intent("I2"));
        var es = t.GetEntries();
        Assert.Equal(new[] { ChatEntryKind.Intent, ChatEntryKind.Reasoning, ChatEntryKind.Assistant, ChatEntryKind.Intent },
            es.Select(x => x.Kind).ToArray());
    }

    [Fact]
    public void AC4_ReasoningUpdateKeepsPosition()
    {
        var t = New();
        t.Apply(Delta("r1", "a"));
        t.Apply(Intent("I"));
        t.Apply(Full("r1", "final"));
        var es = t.GetEntries();
        Assert.Equal(2, es.Count);
        Assert.Equal(ChatEntryKind.Reasoning, es[0].Kind);
        Assert.Equal("final", es[0].Text);
        Assert.Equal(ChatEntryKind.Intent, es[1].Kind);
    }

    // AC-5
    private static List<SessionEvent> Script() => new()
    {
        Intent("Start"),
        Delta("r1", "He"),
        Delta("r1", "llo"),
        Full("r1", "Hello world"),
        Intent("Start"),
        Full("r2", "replay only"),
        Msg("m1", "done"),
    };

    [Fact]
    public void AC5_Replay_MatchesLive()
    {
        var live = New();
        foreach (var e in Script()) live.Apply(e);
        var rep = New();
        rep.Replay(Script());
        Assert.Equal(Snap(live), Snap(rep));
        Assert.Contains(live.GetEntries(), x => x.Kind == ChatEntryKind.Reasoning);
        Assert.Contains(live.GetEntries(), x => x.Kind == ChatEntryKind.Intent);
    }

    [Fact]
    public void AC5_Replay_Subagent()
    {
        var evs = new List<SessionEvent> { Intent("Go", "sa1"), Full("r1", "why", "sa1") };
        var rep = WithSub();
        rep.Replay(evs);
        var es = rep.GetEntries();
        Assert.Equal(2, es.Count);
        Assert.All(es, x => { Assert.Equal("Coder", x.Author); Assert.True(x.IsSubagent); });
    }

    [Fact]
    public void AC5_Replay_ReasoningOnlyDeltas_ShowsReasoning()
    {
        var rep = New();
        rep.Replay(new SessionEvent[] { Delta("r1", "ab"), Delta("r1", "cd") });
        var e = Assert.Single(rep.GetEntries());
        Assert.Equal(ChatEntryKind.Reasoning, e.Kind);
        Assert.Equal("abcd", e.Text);
    }

    private static string Snap(ChatTranscript t) =>
        string.Join("|", t.GetEntries().Select(e => $"{e.Kind}/{e.Author}/{e.Key}/{e.IsSubagent}/{e.Text}"));

    // AC-6
    [Fact]
    public void AC6_AssistantMessage_Unchanged()
    {
        var t = New();
        t.Apply(new AssistantMessageDeltaEvent { Data = new AssistantMessageDeltaData { MessageId = "m1", DeltaContent = "Hi" } });
        t.Apply(Msg("m1", "Hi there"));
        var e = Assert.Single(t.GetEntries());
        Assert.Equal(ChatEntryKind.Assistant, e.Kind);
        Assert.Equal("Hi there", e.Text);
        Assert.Equal("Boss", e.Author);
    }

    [Fact]
    public void AC6_ReasoningDoesNotDisturbAssistantMessage()
    {
        var t = New();
        t.Apply(new AssistantMessageDeltaEvent { Data = new AssistantMessageDeltaData { MessageId = "m1", DeltaContent = "A" } });
        t.Apply(Delta("r1", "think"));
        t.Apply(new AssistantMessageDeltaEvent { Data = new AssistantMessageDeltaData { MessageId = "m1", DeltaContent = "B" } });
        var es = t.GetEntries();
        Assert.Equal(2, es.Count);
        Assert.Equal("AB", es.Single(x => x.Kind == ChatEntryKind.Assistant).Text);
        Assert.Equal("think", es.Single(x => x.Kind == ChatEntryKind.Reasoning).Text);
    }
}
