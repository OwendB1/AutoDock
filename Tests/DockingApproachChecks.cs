using System;
using ClientPlugin;
using VRageMath;

internal static partial class Program
{
    private static void CheckDockingApproach()
    {
        Check(Approach(5, 30).ConstraintPosition.Z, 2);
        Check(Approach(50, 30).ConstraintPosition.Z, 5);
        Check(Approach(1000, 30).ConstraintPosition.Z, 10);
        AutoDockConstants.AutoDockFinalApproachDistance = 7;
        Check(Approach(5, 30).ConstraintPosition.Z, 7);
        AutoDockConstants.AutoDockFinalApproachDistance = 2;

        // Starting inside the waypoint must not command an outward leg when aligned.
        foreach (double length in new[] { 5.0, 50.0, 1000.0 })
        foreach (double gap in new[] { 0.0, 0.1, 0.8, 1.9, -0.8 })
        {
            DockingTarget target = Approach(length, gap);
            CheckVector(target.ConstraintPosition, Vector3D.Zero);
            AssertApproach(target.FinalApproachActive, "Aligned close ship did not enter final approach");
            AssertApproach(target.MaxAngularVelocity <= 0.5 / length, "Close hull rotation is too fast");
        }

        DockingTarget lateral = Approach(50, 1, lateral: 0.3);
        CheckVector(lateral.ConstraintPosition, new Vector3D(0, 0, 1));
        AssertApproach(!lateral.FinalApproachActive && lateral.HoldAxialPosition,
            "Small lateral correction must hold depth inside the waypoint");

        DockingTarget smallTurn = Approach(50, 1, angle: 0.01);
        AssertApproach(smallTurn.FinalApproachActive && smallTurn.MaxAngularVelocity > 0,
            "Small turn with enough clearance should not force a retreat");
        CheckVector(smallTurn.ConstraintPosition, Vector3D.Zero);
        DockingTarget tinyTurn = Approach(50, 0.1, angle: 0.00001);
        AssertApproach(tinyTurn.FinalApproachActive, "Tiny close correction forced an unnecessary retreat");

        foreach (bool alreadyApproaching in new[] { false, true })
        foreach (double side in new[] { -1.0, 1.0 })
        {
            DockingTarget retreat = Approach(50, side, angle: 0.1, finalApproachStarted: alreadyApproaching);
            AssertApproach(!retreat.FinalApproachActive && retreat.MaxAngularVelocity == 0,
                "Unsafe rotation must stop before gaining clearance, including after final approach starts");
            AssertApproach(retreat.ConstraintPosition.Z * side > 5 && retreat.ConstraintPosition.Z * side < 6,
                "Retreat should cover the required hull sweep, not the full ship length");
            CheckVector(retreat.WorldMatrix.Forward, MatrixD.Identity.Forward);
            DockingTarget cleared = Approach(50, retreat.ConstraintPosition.Z, angle: 0.1);
            AssertApproach(cleared.MaxAngularVelocity > 0, "Rotation never resumed after gaining clearance");
            AssertApproach(cleared.MaxAngularVelocity <= 0.01, "Rotation resumed too quickly beside the target");
            AssertApproach(Math.Abs(cleared.ConstraintPosition.Z) >= 5.1,
                "Staging target would close the rotation clearance again");
        }

        DockingTarget halfTurn = Approach(50, 1, angle: Math.PI);
        AssertApproach(!halfTurn.FinalApproachActive && !halfTurn.DockingReady && halfTurn.MaxAngularVelocity == 0,
            "Half-turn was incorrectly treated as alignment");

        // The target frame must keep an offset connector anchored, including while rotation is blocked.
        MatrixD current = MatrixD.CreateRotationX(0.6);
        current.Translation = new Vector3D(100, 200, 300);
        MatrixD desired = MatrixD.CreateRotationY(0.1) * current;
        Vector3D localConnector = new Vector3D(5, -2, 7);
        Vector3D connector = Vector3D.Transform(localConnector, current);
        Vector3D axis = Vector3D.Normalize(new Vector3D(1, 2, 3));
        foreach (double gap in new[] { 1.0, 30.0 })
        {
            DockingTarget transformed = DockingTarget.Create(current, desired, Bounds(50), connector,
                connector - axis * gap, axis, false);
            CheckVector(Vector3D.Transform(localConnector, transformed.WorldMatrix), transformed.ConstraintPosition);
            AssertApproach(transformed.WorldMatrix.IsValid(), "Invalid transformed docking frame");
        }
        DockingTarget rotatedAligned = DockingTarget.Create(current, current, Bounds(50), connector,
            connector - axis * 0.1, axis, false);
        AssertApproach(rotatedAligned.FinalApproachActive, "Rotated world frame introduced a false alignment error");
    }

    private static DockingTarget Approach(double length, double gap, double angle = 0, double lateral = 0,
        bool finalApproachStarted = false)
    {
        return DockingTarget.Create(MatrixD.Identity, MatrixD.CreateRotationY(angle), Bounds(length),
            new Vector3D(lateral, 0, gap), Vector3D.Zero, Vector3D.Backward, finalApproachStarted);
    }

    private static BoundingBox Bounds(double length)
    {
        return new BoundingBox(Vector3.Zero, new Vector3((float)length, 0, 0));
    }

    private static void AssertApproach(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}

// Planner settings only; no game session or configuration UI is needed for these checks.
namespace ClientPlugin
{
    internal static class AutoDockConstants
    {
        public static double AutoDockFinalApproachDistance = 2.0;
        public const double AutoDockFinalApproachEntryDistanceTolerance = 0.35;
        public const double AutoDockFinalApproachPlanarTolerance = 0.25;
        public const double AutoDockFinalApproachOrientationThreshold = 0.35;
        public const double AutoDockPositionTolerance = 0.12;
        public const double AutoDockOrientationTolerance = 0.01;
        public const double AutoDockLinearBrakePadding = 0.05;
        public const double AutoDockMaxAngularVelocity = 0.65;
    }
}
