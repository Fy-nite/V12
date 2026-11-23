using System;
using System.Numerics;
using V12.Basic.Components;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace SpaceFlight.Components;

/// <summary>
/// A small navball fixed in camera space but drawn in the ship's frame. The
/// ball is a plain sphere; the horizon and meridian are projected curves on
/// its visible disk, and the prograde/retrograde markers follow the velocity.
/// </summary>
public sealed class NavballComponent : ScriptableComponent
{
    public IWorldElement? Ship { get; set; }
    public GravityBodySet? Bodies { get; set; }
    public NavballCurveMesh? HorizonCurve { get; set; }
    public NavballCurveMesh? MeridianCurve { get; set; }
    public IWorldElement? ProgradeMarker { get; set; }
    public IWorldElement? RetrogradeMarker { get; set; }

    public Vector3 LocalPosition { get; set; } = new(0.52f, -0.36f, -1.6f);
    public float LocalScale { get; set; } = 0.40f;

    public override void Update(float deltaTime)
    {
        if (Owner == null)
            return;

        var body = Ship?.GetComponent<PhysicsBodyComponent>()?.Body;
        var shipRotation = body?.Rotation
                           ?? Ship?.LocalTransform.Rotation
                           ?? Quaternion.Identity;

        Owner.LocalTransform = new TRS
        {
            Position = LocalPosition,
            Rotation = Quaternion.Identity,
            Scale = new Vector3(LocalScale),
        };

        // The navball is a ship-relative instrument: the centre of the disk is
        // the nose, so the camera can orbit freely without moving the horizon.
        var velocity = body?.LinearVelocity ?? Vector3.Zero;
        var position = body?.Position ?? Vector3.Zero;
        var inverseShip = Quaternion.Conjugate(shipRotation);

        // Horizon is local to whichever planet dominates gravity right now,
        // not always the world-origin planet.
        var localBody = Bodies?.FindDominant(position);
        var center = localBody?.WorldPosition ?? Vector3.Zero;
        var relativePosition = position - center;
        var relativeVelocity = velocity - (Bodies?.VelocityOf(localBody) ?? Vector3.Zero);

        var orbitUp = Vector3.Cross(relativeVelocity, relativePosition);
        orbitUp = orbitUp.LengthSquared() > 1e-6f
            ? Vector3.Normalize(orbitUp)
            : Vector3.Transform(Vector3.UnitY, shipRotation);

        var forward = Vector3.Transform(-Vector3.UnitZ, shipRotation);
        var meridianNormal = Vector3.Cross(forward, orbitUp);
        if (meridianNormal.LengthSquared() < 1e-6f)
            meridianNormal = Vector3.Transform(Vector3.UnitX, shipRotation);

        if (HorizonCurve != null)
            HorizonCurve.PlaneNormal = Vector3.Transform(orbitUp, inverseShip);
        if (MeridianCurve != null)
            MeridianCurve.PlaneNormal = Vector3.Transform(meridianNormal, inverseShip);

        if (relativeVelocity.LengthSquared() > 1e-6f)
        {
            var prograde = Vector3.Transform(
                Vector3.Normalize(relativeVelocity), inverseShip);
            SetMarker(ProgradeMarker, prograde);
            SetMarker(RetrogradeMarker, -prograde);
        }
        else
        {
            Hide(ProgradeMarker);
            Hide(RetrogradeMarker);
        }
    }

    /// <summary>
    /// Project a ship-local unit direction onto the navball disk. The centre
    /// of the disk is the nose, so a prograde direction dead ahead sits in the
    /// middle; directions at or behind the rim are clamped to the edge and
    /// hidden once they pass behind the instrument.
    /// </summary>
    private void SetMarker(IWorldElement? marker, Vector3 direction)
    {
        if (marker == null)
            return;

        if (direction.Z >= 0f)
        {
            Hide(marker);
            return;
        }

        float x = direction.X;
        float y = direction.Y;
        float radius = MathF.Sqrt(x * x + y * y);
        if (radius > 1f)
        {
            x /= radius;
            y /= radius;
        }

        // Markers sit a little in front of the ball so they never z-fight with
        // it. Compensate the offset and size for the slightly nearer plane so
        // a rim marker still lands on the silhouette.
        float depth = MathF.Max(0.01f, -LocalPosition.Z);
        float markerZ = 0.51f;
        float apparent = (depth - markerZ * LocalScale) / depth;

        SetActive(marker, true);
        marker.LocalTransform = new TRS
        {
            Position = new Vector3(
                x * 0.5f * apparent,
                y * 0.5f * apparent,
                markerZ),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
            Scale = new Vector3(apparent),
        };
    }

    private static void Hide(IWorldElement? marker)
    {
        if (marker == null)
            return;

        // Active alone is not enough for a container whose children own the
        // renderers, so move the whole marker behind the camera as well.
        marker.LocalTransform = new TRS
        {
            Position = new Vector3(0f, 0f, 1000f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        SetActive(marker, false);
    }

    /// <summary>
    /// Marker elements are containers (ring + dot), so a parent-only Active
    /// flag is not enough: every child renderer must be disabled too. Active
    /// does not raise the element dirty event, so a state change also requests
    /// a render capture or the culler keeps the old snapshot.
    /// </summary>
    private static void SetActive(IWorldElement? element, bool active)
    {
        bool changed = false;
        SetActiveRecursive(element, active, ref changed);
        if (changed)
            GameRoot.Instance?.MarkRenderDirty();
    }

    private static void SetActiveRecursive(
        IWorldElement? element, bool active, ref bool changed)
    {
        if (element == null)
            return;

        if (element.Active != active)
        {
            element.Active = active;
            changed = true;
        }

        foreach (var child in element.Children)
            SetActiveRecursive(child, active, ref changed);
    }
}
