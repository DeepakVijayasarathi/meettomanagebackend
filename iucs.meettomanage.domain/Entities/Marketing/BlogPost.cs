using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Marketing
{
    /// <summary>
    /// A post on the public marketing blog (/blog). Content is stored as a single
    /// markdown-lite string rather than a structured block schema, deliberately: a
    /// blank line separates paragraphs, and a line starting with "## " is a heading —
    /// simple enough for the admin editor to be one textarea, and for the public page
    /// to parse without a markdown dependency.
    /// </summary>
    [Index(nameof(Slug), IsUnique = true)]
    public class BlogPost : AuditEntity
    {
        [MaxLength(200)]
        public string Title { get; set; } = null!;

        /// <summary>URL segment (/blog/{slug}) — unique, lowercase-hyphenated.</summary>
        [MaxLength(200)]
        public string Slug { get; set; } = null!;

        [MaxLength(500)]
        public string Excerpt { get; set; } = null!;

        public string Content { get; set; } = null!;

        public int ReadMinutes { get; set; }

        public bool IsPublished { get; set; }

        /// <summary>Set the first time a post is published; unaffected by later edits.</summary>
        public DateTime? PublishedAtUtc { get; set; }
    }
}
