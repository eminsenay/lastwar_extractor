namespace LastWarExtractor.Models;

public sealed class Member
{
    public Member(int memberId, string name, string rank, DateTime? joinedOn, double? totalHeroPower)
    {
        MemberId = memberId;
        Name = name;
        Rank = rank;
        JoinedOn = joinedOn;
        TotalHeroPower = totalHeroPower;
    }

    public int MemberId { get; }
    public string Name { get; }
    public string Rank { get; }
    public DateTime? JoinedOn { get; }
    public double? TotalHeroPower { get; }
}

public sealed class MemberLoadResult
{
    public MemberLoadResult(List<Member> members, List<Member> allRows, List<string> warnings, string sourceDescription)
    {
        Members = members;
        AllRows = allRows;
        Warnings = warnings;
        SourceDescription = sourceDescription;
    }

    public List<Member> Members { get; }
    public List<Member> AllRows { get; }
    public List<string> Warnings { get; }
    public string SourceDescription { get; set; }
}
