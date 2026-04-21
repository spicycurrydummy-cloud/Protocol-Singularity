namespace ProtocolSingularity.Core
{
    public enum GamePhase
    {
        Title = 0,
        Lobby = 1,
        GameStart = 10,
        TeamProposal = 20,
        ApprovalVote = 21,
        Hacking = 22,
        RoundResult = 23,
        OverrideDiscussion = 30,
        OverrideVote = 31,
        OverrideResult = 32,
        GameEnd = 99
    }
}
