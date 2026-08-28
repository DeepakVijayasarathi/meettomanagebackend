using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Quizzes
{
    /// <summary>One answer choice for a <see cref="QuizQuestion"/>. Exactly one option per
    /// question is <see cref="IsCorrect"/> — enforced by QuizQuestionService, not the schema.</summary>
    [Index(nameof(QuizQuestionId))]
    public class QuizQuestionOption : BaseEntity
    {
        public Guid QuizQuestionId { get; set; }

        public QuizQuestion QuizQuestion { get; set; } = null!;

        [MaxLength(200)]
        public string Text { get; set; } = null!;

        public bool IsCorrect { get; set; }

        public int DisplayOrder { get; set; }
    }
}
