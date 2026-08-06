namespace Crm;

/// <summary>How credit for a deal is shared out among the campaigns that touched it.</summary>
/// <remarks>
/// <para>
/// Closed, and named in every answer. Credit is not a fact about a deal — it is the output of a
/// model somebody chose, and four reasonable people choose four different models. A number quoted
/// without the model that produced it is a number two departments can argue about for a quarter
/// while both are right.
/// </para>
/// </remarks>
public enum AttributionModel
{
    /// <summary>All of it to the campaign that got there first.</summary>
    /// <remarks>Flatters whoever fills the top of the funnel.</remarks>
    FirstTouch,

    /// <summary>All of it to the campaign that got there last.</summary>
    /// <remarks>Flatters whoever sent the final email.</remarks>
    LastTouch,

    /// <summary>An equal share to every campaign that touched it.</summary>
    /// <remarks>Flatters everybody equally and nobody in particular.</remarks>
    Linear,

    /// <summary>Two fifths to the first, two fifths to the last, the rest shared.</summary>
    /// <remarks>
    /// The model most organisations settle on, because it says out loud what the other three imply:
    /// that finding somebody and closing them are both worth more than the emails in between.
    /// </remarks>
    PositionBased,
}

/// <summary>One interaction between a campaign and a person.</summary>
/// <param name="CampaignId">Which campaign.</param>
/// <param name="Campaign">What it is called.</param>
/// <param name="TouchedAt">When.</param>
public sealed record Touch(Guid CampaignId, string Campaign, DateTimeOffset TouchedAt);

/// <summary>What one campaign was given of one deal.</summary>
/// <param name="CampaignId">Which campaign.</param>
/// <param name="Campaign">What it is called.</param>
/// <param name="Amount">How much of the deal.</param>
/// <param name="Share">What fraction that is, between zero and one.</param>
public sealed record AttributedCredit(Guid CampaignId, string Campaign, decimal Amount, double Share);

/// <summary>
/// Shares a deal out among the campaigns that touched it, under a model somebody named.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The credit sums to the deal, to the penny.</strong> The quiet way an attribution report
/// stops being read is rounding: three campaigns each given a third of a deal worth one hundred
/// leave a penny nobody has, the campaign totals do not add to the pipeline total, and the first
/// person to notice stops trusting every other number on the page. The remainder is given to the
/// last campaign rather than dropped, and there is a test whose only job is to fail if the total
/// ever drifts.
/// </para>
/// <para>
/// <strong>A touch after the deal closed did not win the deal.</strong> A campaign that emailed a
/// customer a month after they bought is claiming credit for a decision already made, and a
/// last-touch model with no cutoff will give it all of one. Filtered here rather than in the
/// query, because every model needs the same cutoff and one of them would eventually be written
/// without it.
/// </para>
/// </remarks>
public static class Attribution
{
    /// <summary>What the first and last campaign get under <see cref="AttributionModel.PositionBased"/>.</summary>
    public const double PositionWeight = 0.4;

    /// <summary>Shares a deal out.</summary>
    /// <param name="model">Which model.</param>
    /// <param name="touches">Everything that touched the person, in any order.</param>
    /// <param name="amount">What the deal is worth.</param>
    /// <param name="decidedAt">
    /// When the deal was decided. Touches after it are dropped, because they cannot have
    /// contributed to a decision already taken.
    /// </param>
    /// <returns>
    /// What each campaign was given, largest first. Empty when nothing touched the person before
    /// the deal was decided — which is a real answer and means the deal was not influenced, not
    /// that the query failed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="touches"/> is null.</exception>
    public static IReadOnlyList<AttributedCredit> Split(
        AttributionModel model,
        IReadOnlyList<Touch> touches,
        decimal amount,
        DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(touches);

        // One campaign that touched somebody five times is one campaign, not five. Keeping the
        // earliest touch of each is what makes "first touch" mean the first campaign rather than
        // the first email, which is the thing anybody asking the question meant.
        var ordered = touches
            .Where(touch => touch.TouchedAt <= decidedAt)
            .GroupBy(touch => touch.CampaignId)
            .Select(group => group.OrderBy(touch => touch.TouchedAt).First())
            .OrderBy(touch => touch.TouchedAt)
            .ThenBy(touch => touch.CampaignId)
            .ToList();

        if (ordered.Count == 0)
        {
            return [];
        }

        var shares = Shares(model, ordered.Count);
        var credits = new List<AttributedCredit>(ordered.Count);
        var given = 0m;

        for (var index = 0; index < ordered.Count; index++)
        {
            // The last campaign gets whatever is left rather than its own rounded share. Three
            // thirds of one hundred is the case: 33.33 + 33.33 + 33.33 is not one hundred, and the
            // missing penny is what makes a campaign total disagree with a pipeline total.
            var credit = index == ordered.Count - 1
                ? amount - given
                : decimal.Round(amount * (decimal)shares[index], 4, MidpointRounding.ToEven);

            given += credit;

            credits.Add(new AttributedCredit(
                ordered[index].CampaignId, ordered[index].Campaign, credit, shares[index]));
        }

        return [.. credits.OrderByDescending(credit => credit.Amount)
            .ThenBy(credit => credit.Campaign, StringComparer.Ordinal)];
    }

    /// <summary>What fraction each position gets, in touch order.</summary>
    private static double[] Shares(AttributionModel model, int count)
    {
        if (count == 1)
        {
            // Every model agrees when there is one campaign, and saying so once here is why none
            // of the branches below has to defend against a single-element list.
            return [1];
        }

        var shares = new double[count];

        if (model == AttributionModel.FirstTouch)
        {
            shares[0] = 1;

            return shares;
        }

        if (model == AttributionModel.LastTouch)
        {
            shares[^1] = 1;

            return shares;
        }

        if (model == AttributionModel.PositionBased && count == 2)
        {
            // With nothing in between, the two positions share it evenly rather than leaving a
            // fifth for a middle that does not exist.
            return [0.5, 0.5];
        }

        // Linear gives everybody the same; position-based gives everybody the same and then pays
        // the ends more out of what is left. One loop, because they differ only in what the middle
        // is worth.
        var middle = model == AttributionModel.PositionBased
            ? (1 - (2 * PositionWeight)) / (count - 2)
            : 1d / count;

        for (var index = 0; index < count; index++)
        {
            shares[index] = middle;
        }

        if (model == AttributionModel.PositionBased)
        {
            shares[0] = PositionWeight;
            shares[^1] = PositionWeight;
        }

        return shares;
    }
}
