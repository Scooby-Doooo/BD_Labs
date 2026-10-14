namespace FreelanceLab;

public record Project(long Id, long ClientId, string Title, decimal Budget, DateOnly Deadline, string Status, DateTime UpdatedAt, long UpdatedBy);
public record Proposal(long Id, long ProjectId, long FreelancerId, string FreelancerName, decimal Amount, int Days, string Status, DateTime UpdatedAt, long UpdatedBy);
public record Contract(long Id, long ProjectId, string ProjectTitle, long ClientId, long FreelancerId, decimal Amount, string Status, decimal Paid, decimal Outstanding);
public record Milestone(long Id, long ContractId, string Title, decimal Amount, DateOnly DueDate, string Status, decimal Paid);
public record PortfolioItem(long Id, long FreelancerId, string Title, DateTime UpdatedAt, long UpdatedBy);
public record User(long Id, string Name);
public record Review(long Id, long ContractId, long AuthorId, short Rating, string Comment);
public record Message(long Id, long ContractId, long SenderId, long RecipientId, string Body);
public record Submission(long Id, long MilestoneId, string Description);
public record MilestoneInput(string Title, decimal Amount, DateOnly DueDate);
