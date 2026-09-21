using NXOpen;
using NXOpen.Features;
using BANxOpen.Foundation.NxAdapters;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>Where the dialog's direction arrow goes: at the start of a bead's first curve, along the normal of the
/// body face that curve lies on. Only places the arrow — the side a bead is built to is the builder's
/// <c>HeightSide</c>, set from the arrow's flip. Every failure returns null, which hides the arrow: a missing
/// arrow must never stop the dialog.</summary>
public sealed class BeadDirectionProbe
{
    private readonly NxSessionContext _context;

    public BeadDirectionProbe(NxSessionContext context) => _context = context;

    /// <param name="chain">The bead's curves; empty for an existing bead, whose own section is read instead.</param>
    public (Point3d Origin, Vector3d Normal)? Find(IReadOnlyList<NXObject> chain, Feature? existingFeature, Body body)
    {
        try
        {
            var curve = chain.FirstOrDefault() ?? FirstSectionCurve(existingFeature);
            if (curve is null)
                return null;

            var point = new double[3];
            var scratch = new double[3];
            _context.UFSession.Modl.AskCurveProps(curve.Tag, 0.0, point, scratch, new double[3], new double[3], out _, out _);

            return NormalOfNearestFace(body, point) is { } normal
                ? (new Point3d(point[0], point[1], point[2]), normal)
                : null;
        }
        catch (NXException ex)
        {
            _context.Log.Warn($"Could not place the direction arrow: NX {ex.ErrorCode}: {ex.Message}");
            return null;
        }
    }

    private static NXObject? FirstSectionCurve(Feature? feature)
    {
        if (feature is null)
            return null;

        foreach (var section in feature.GetSections())
        {
            section.GetOutputCurves(out var curves);
            if (curves.FirstOrDefault() is { } curve)
                return curve;
        }

        return null;
    }

    private Vector3d? NormalOfNearestFace(Body body, double[] point)
    {
        var modl = _context.UFSession.Modl;
        Face? nearest = null;
        var nearestDistance = double.MaxValue;
        var onFace = new double[3];

        foreach (var face in body.GetFaces())
        {
            // A null second object makes NX measure to the guess point instead.
            var candidate = new double[3];
            modl.AskMinimumDist(face.Tag, Tag.Null, 0, new double[3], 1, point, out var distance, candidate, new double[3]);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = face;
                onFace = candidate;
            }
        }

        if (nearest is null)
            return null;

        var parameter = new double[2];
        modl.AskFaceParm(nearest.Tag, onFace, parameter, new double[3]);

        var normal = new double[3];
        modl.AskFaceProps(nearest.Tag, parameter, new double[3], new double[3], new double[3], new double[3], new double[3], normal, new double[2]);
        return new Vector3d(normal[0], normal[1], normal[2]);
    }
}
