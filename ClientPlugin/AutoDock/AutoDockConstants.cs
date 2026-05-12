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
    public static int AutoDockTimeoutFrames => GetPositiveInt(Config.Current.AutoDockTimeoutFrames, Config.Default.AutoDockTimeoutFrames);
    public static double AutoDockPositionTolerance => GetFiniteDouble(Config.Current.AutoDockPositionTolerance, Config.Default.AutoDockPositionTolerance, 0.0);
    public static double AutoDockOrientationTolerance => GetFiniteDouble(Config.Current.AutoDockOrientationTolerance, Config.Default.AutoDockOrientationTolerance, 0.0);
    public static double AutoDockControlStepSeconds => GetFiniteDouble(Config.Current.AutoDockControlStepSeconds, Config.Default.AutoDockControlStepSeconds, 0.0001);
    public static double AutoDockLinearProportionalGain => GetFiniteDouble(Config.Current.AutoDockLinearProportionalGain, Config.Default.AutoDockLinearProportionalGain, 0.0);
    public static double AutoDockLinearIntegralGain => GetFiniteDouble(Config.Current.AutoDockLinearIntegralGain, Config.Default.AutoDockLinearIntegralGain, 0.0);
    public static double AutoDockLinearDerivativeGain => GetFiniteDouble(Config.Current.AutoDockLinearDerivativeGain, Config.Default.AutoDockLinearDerivativeGain, 0.0);
    public static double AutoDockLinearIntegralLimit => GetFiniteDouble(Config.Current.AutoDockLinearIntegralLimit, Config.Default.AutoDockLinearIntegralLimit, 0.0);
    public static double AutoDockLinearIntegralActivationDistance => GetFiniteDouble(Config.Current.AutoDockLinearIntegralActivationDistance, Config.Default.AutoDockLinearIntegralActivationDistance, 0.0);
    public static double AutoDockLinearBrakePadding => GetFiniteDouble(Config.Current.AutoDockLinearBrakePadding, Config.Default.AutoDockLinearBrakePadding, 0.0);
    public static double AutoDockMaxLinearAcceleration => GetFiniteDouble(Config.Current.AutoDockMaxLinearAcceleration, Config.Default.AutoDockMaxLinearAcceleration, 0.0001);
    public static double AutoDockFinalApproachMaxLinearAcceleration => GetFiniteDouble(Config.Current.AutoDockFinalApproachMaxLinearAcceleration, Config.Default.AutoDockFinalApproachMaxLinearAcceleration, 0.0001);
    public static double AutoDockFinalApproachDistance => GetFiniteDouble(Config.Current.AutoDockFinalApproachDistance, Config.Default.AutoDockFinalApproachDistance, 0.0001);
    public static double AutoDockFinalApproachEntryDistanceTolerance => GetFiniteDouble(Config.Current.AutoDockFinalApproachEntryDistanceTolerance, Config.Default.AutoDockFinalApproachEntryDistanceTolerance, 0.0);
    public static double AutoDockFinalApproachPlanarTolerance => GetFiniteDouble(Config.Current.AutoDockFinalApproachPlanarTolerance, Config.Default.AutoDockFinalApproachPlanarTolerance, 0.0);
    public static double AutoDockFinalApproachOrientationThreshold => GetFiniteDouble(Config.Current.AutoDockFinalApproachOrientationThreshold, Config.Default.AutoDockFinalApproachOrientationThreshold, 0.0);
    public static double AutoDockAngularProportionalGain => GetFiniteDouble(Config.Current.AutoDockAngularProportionalGain, Config.Default.AutoDockAngularProportionalGain, 0.0);
    public static double AutoDockAngularIntegralGain => GetFiniteDouble(Config.Current.AutoDockAngularIntegralGain, Config.Default.AutoDockAngularIntegralGain, 0.0);
    public static double AutoDockAngularIntegralLimit => GetFiniteDouble(Config.Current.AutoDockAngularIntegralLimit, Config.Default.AutoDockAngularIntegralLimit, 0.0);
    public static double AutoDockMaxAngularVelocity => GetFiniteDouble(Config.Current.AutoDockMaxAngularVelocity, Config.Default.AutoDockMaxAngularVelocity, 0.0001);
    public static double AutoDockAngularVelocityGain => GetFiniteDouble(Config.Current.AutoDockAngularVelocityGain, Config.Default.AutoDockAngularVelocityGain, 0.0001);
    public static double AutoDockAngularSofteningThreshold => GetFiniteDouble(Config.Current.AutoDockAngularSofteningThreshold, Config.Default.AutoDockAngularSofteningThreshold, 0.0001);
    public static float AutoDockAngularMinTorque => MathHelper.Clamp(
        GetFiniteFloat(Config.Current.AutoDockAngularMinTorque, Config.Default.AutoDockAngularMinTorque, 0f),
        0f,
        AutoDockAngularMaxTorque);
    public static float AutoDockAngularMaxTorque => GetFiniteFloat(Config.Current.AutoDockAngularMaxTorque, Config.Default.AutoDockAngularMaxTorque, 0.0001f);
    public const int AutoDockLockDelayFrames = 80;
    public const double ManualInputDeadzone = 0.12;
    public const double MaxGravityTiltWarningRadians = Math.PI / 4.0;
    public const int LockRotationStepCount = 8;
    public const double LockRotationStepRadians = Math.PI * 2.0 / LockRotationStepCount;
    public const int AutoLandingTimeoutFrames = 60 * 25;
    public const int AutoLandingPostLockFrames = 120;
    public const double AutoLandingRaycastRange = 120.0;
    public const double AutoLandingRayStartOffset = 0.35;
    public const double AutoLandingTargetOverlap = 0.12;
    public const double AutoLandingContactTolerance = 0.22;
    public const double AutoLandingHullClearance = 0.18;
    public const double AutoLandingHullHardpointExclusionRadius = 0.75;
    public const double AutoLandingMinGravity = 0.1;
    public const double AutoLandingMaxLinearAcceleration = 1.5;
    public const double AutoLandingMaxAngularVelocity = 0.35;

    public static float GetSearchRadius()
    {
        float radius = Config.Current.ConnectorSearchRadius;
        if (float.IsNaN(radius) || float.IsInfinity(radius))
            radius = 5f;

        return MathHelper.Clamp(radius, MinSearchRadius, MaxSearchRadius);
    }

    private static int GetPositiveInt(int value, int fallback, int minimum = 1)
    {
        return value >= minimum ? value : fallback;
    }

    private static double GetFiniteDouble(double value, double fallback, double minimum)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < minimum
            ? fallback
            : value;
    }

    private static float GetFiniteFloat(float value, float fallback, float minimum)
    {
        return float.IsNaN(value) || float.IsInfinity(value) || value < minimum
            ? fallback
            : value;
    }
}
