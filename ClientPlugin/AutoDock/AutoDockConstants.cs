using System;
using VRageMath;

namespace ClientPlugin;

internal static class AutoDockConstants
{
    public const int RescanIntervalFrames = 10;
    public const float SoftSelectRadius = 100f;
    public const float MinSearchRadius = 1f;
    public const float MaxSearchRadius = 30f;
    public const double MinConnectorDistanceSquared = 0.0001;
    public const int AutoDockTimeoutFrames = 60 * 15;
    public const double AutoDockPositionTolerance = 0.12;
    public const double AutoDockOrientationTolerance = 0.01;
    public const double AutoDockControlStepSeconds = 1.0 / 60.0;
    public const double AutoDockLinearProportionalGain = 0.65;
    public const double AutoDockLinearIntegralGain = 0.22;
    public const double AutoDockLinearDerivativeGain = 1.35;
    public const double AutoDockLinearIntegralLimit = 6.0;
    public const double AutoDockLinearIntegralActivationDistance = 5.0;
    public const double AutoDockLinearBrakePadding = 0.05;
    public const double AutoDockMaxLinearAcceleration = 4.0;
    public const double AutoDockFinalApproachMaxLinearAcceleration = 3.0;
    public const double AutoDockFinalApproachDistance = 2.0;
    public const double AutoDockFinalApproachEntryDistanceTolerance = 0.35;
    public const double AutoDockFinalApproachPlanarTolerance = 0.25;
    public const double AutoDockFinalApproachOrientationThreshold = 0.35;
    public const double AutoDockAngularProportionalGain = 2.0;
    public const double AutoDockAngularIntegralGain = 0.08;
    public const double AutoDockAngularIntegralLimit = 0.35;
    public const double AutoDockMaxAngularVelocity = 0.65;
    public const double AutoDockAngularVelocityGain = 1.8;
    public const double AutoDockAngularSofteningThreshold = 0.35;
    public const float AutoDockAngularMinTorque = 0.35f;
    public const float AutoDockAngularMaxTorque = 0.8f;
    public const int AutoDockLockDelayFrames = 20;
    public const double ManualInputDeadzone = 0.12;
    public const double MaxGravityTiltWarningRadians = Math.PI / 4.0;
    public const int LockRotationStepCount = 8;
    public const double LockRotationStepRadians = Math.PI * 2.0 / LockRotationStepCount;

    public static float GetSearchRadius()
    {
        float radius = Config.Current.ConnectorSearchRadius;
        if (float.IsNaN(radius) || float.IsInfinity(radius))
            radius = 5f;

        return MathHelper.Clamp(radius, MinSearchRadius, MaxSearchRadius);
    }
}
