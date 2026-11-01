namespace Restaurant.Api;

public sealed record OrderDeadlines(DateTimeOffset ChangesClose, DateTimeOffset SubmissionCloses)
{
    public static OrderDeadlines FromReservation(DateTimeOffset reservationStart, TimeSpan preparationTime, TimeSpan reviewTime)
    {
        if (preparationTime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(preparationTime));
        if (reviewTime < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(reviewTime));
        var submissionCloses = reservationStart.Subtract(preparationTime);
        return new(submissionCloses.Subtract(reviewTime), submissionCloses);
    }

    public bool CanChange(DateTimeOffset now) => now < ChangesClose;
    public bool CanSubmit(DateTimeOffset now) => now < SubmissionCloses;
}
