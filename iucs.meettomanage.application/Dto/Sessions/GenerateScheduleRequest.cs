using System.ComponentModel.DataAnnotations;

namespace iucs.meettomanage.application.Dto.Sessions
{
    /// <summary>
    /// Automated scheduling: generates every session of a batch's course on the
    /// selected weekdays at a fixed time, skipping holidays, until the course's
    /// TotalSessions count is reached.
    /// </summary>
    public class GenerateScheduleRequest
    {
        [Required]
        public DateOnly StartDate { get; set; }

        [Required]
        [MinLength(1)]
        public List<DayOfWeek> DaysOfWeek { get; set; } = [];

        [Required]
        public TimeOnly StartTimeUtc { get; set; }
    }
}
