using System.Collections.Immutable;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// A proposed hold generated automatically (Phase 31, D047). It is an ephemeral candidate: not persisted in the project,
/// reviewed by the user and either refined into a <see cref="Hold"/> or discarded.
/// </summary>
public sealed class HoldProposal
{
    public HoldProposal(
        Guid id,
        ImmutableArray<NormalizedPoint> contour,
        NormalizedPoint centroid,
        double score,
        double areaFraction)
    {
        if (contour.IsDefault) contour = ImmutableArray<NormalizedPoint>.Empty;
        if (contour.Length < HoldMarkupRules.MinPoints)
            throw new ArgumentException($"A proposal contour must have at least {HoldMarkupRules.MinPoints} points.", nameof(contour));
        if (score < 0 || score > 1) throw new ArgumentOutOfRangeException(nameof(score));
        if (areaFraction < 0 || areaFraction > 1) throw new ArgumentOutOfRangeException(nameof(areaFraction));

        Id = id;
        Contour = contour;
        Centroid = centroid;
        Score = score;
        AreaFraction = areaFraction;
    }

    public Guid Id { get; }

    /// <summary>The proposal's outer contour in normalized source coordinates (0..1).</summary>
    public ImmutableArray<NormalizedPoint> Contour { get; }

    /// <summary>The proposal's centre in normalized source coordinates, used for full-resolution refinement.</summary>
    public NormalizedPoint Centroid { get; }

    /// <summary>The model's predicted IoU for the chosen mask.</summary>
    public double Score { get; }

    /// <summary>The fraction of the proposal frame the mask occupies.</summary>
    public double AreaFraction { get; }
}

/// <summary>Parameters for the automatic hold-proposal pipeline (Phase 31, D047).</summary>
public sealed record ProposalPipelineOptions
{
    /// <summary>N×N grid of positive prompts. Default 15 (225 points).</summary>
    public int GridDivisions { get; init; } = 15;

    /// <summary>Mask-IoU threshold for greedy duplicate suppression. Default 0.70.</summary>
    public double NmsIouThreshold { get; init; } = 0.70;

    /// <summary>Maximum number of proposals to return. Default 30.</summary>
    public int MaxProposals { get; init; } = 30;

    /// <summary>The minimum allowed value for <see cref="MaxProposals"/>.</summary>
    public const int MinMaxProposals = 10;

    /// <summary>The maximum allowed value for <see cref="MaxProposals"/>.</summary>
    public const int MaxMaxProposals = 100;

    public ProposalPipelineOptions Validate()
    {
        if (GridDivisions < 2 || GridDivisions > 64)
            throw new ArgumentOutOfRangeException(nameof(GridDivisions), "GridDivisions must be between 2 and 64.");
        if (NmsIouThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(NmsIouThreshold), "NMS IoU must be between 0 and 1.");
        if (MaxProposals < MinMaxProposals || MaxProposals > MaxMaxProposals)
            throw new ArgumentOutOfRangeException(nameof(MaxProposals), $"MaxProposals must be between {MinMaxProposals} and {MaxMaxProposals}.");
        return this;
    }
}

/// <summary>The result of an automatic hold-proposal pass (Phase 31, D047).</summary>
public sealed record ProposalGenerationResult(
    IReadOnlyList<HoldProposal> Proposals,
    int GridPoints)
{
    /// <summary>Timing breakdown when the generator was asked to collect diagnostics.</summary>
    public ProposalGenerationDiagnostics? Diagnostics { get; init; }
}

/// <summary>Timing breakdown for a proposal-generation pass (Phase 31 profiling).</summary>
/// <param name="DecodeMs">Frame decode at proposal-pass resolution.</param>
/// <param name="PrepareFrameMs">Preprocess + encoder run.</param>
/// <param name="PredictionLoopMs">All decoder calls and per-mask work.</param>
/// <param name="PredictionCount">Number of grid points evaluated.</param>
/// <param name="PostProcessMs">Contour extraction and centroid after each prediction.</param>
/// <param name="NmsMs">Duplicate suppression.</param>
/// <param name="TotalMs">End-to-end wall time.</param>
public sealed record ProposalGenerationDiagnostics(
    double DecodeMs,
    double PrepareFrameMs,
    double PredictionLoopMs,
    int PredictionCount,
    double PostProcessMs,
    double NmsMs,
    double TotalMs);
