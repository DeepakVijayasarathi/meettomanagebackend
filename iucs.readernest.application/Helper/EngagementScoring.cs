namespace iucs.readernest.application.Helper
{
    /// <summary>
    /// Single source of truth for turning a set of engagement signal totals (already
    /// summed for one session/participant) into the 0-100 "engagement score" shown on
    /// both the live session summary and the student analytics screen. Previously each
    /// screen had its own independently-written formula (different caps-application
    /// order, one silently missing the attention-ping term) that could disagree for the
    /// same underlying data — this is the one place both now call.
    /// </summary>
    public static class EngagementScoring
    {
        /// <summary>Weighted score for a single session: accuracy counts double; each signal is capped so one hyperactive signal can't mask absence everywhere else.</summary>
        public static int Score(int quizCorrect, int quizAttempts, int activity, int whiteboard, int attention)
        {
            return Math.Min(100,
                Math.Min(quizCorrect * 2, 30)
                + Math.Min(quizAttempts, 20)
                + Math.Min(activity * 2, 20)
                + Math.Min(whiteboard, 15)
                + Math.Min(attention, 15));
        }
    }
}
