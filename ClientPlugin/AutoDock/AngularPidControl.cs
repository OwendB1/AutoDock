using VRageMath;

namespace ClientPlugin;

internal struct AngularPidControl
{
    private Vector3D integral;
    private Vector3D previousError;
    private bool hasPreviousError;

    public Vector3D Update(
        Vector3D error,
        Vector3D angularVelocity,
        double stepSeconds,
        double proportionalGain,
        double integralGain,
        double derivativeGain,
        double integralLimit,
        double maxVelocity,
        double velocityGain)
    {
        Vector3D derivative = hasPreviousError ? (error - previousError) / stepSeconds : Vector3D.Zero;
        previousError = error;
        hasPreviousError = true;
        integral = Vector3D.ClampToSphere(integral + error * stepSeconds, integralLimit);

        Vector3D targetVelocity = Vector3D.ClampToSphere(
            error * proportionalGain + integral * integralGain + derivative * derivativeGain,
            maxVelocity);

        // The native gyro loop converts this one-step velocity change to inertia-scaled torque.
        return Vector3D.Lerp(angularVelocity, targetVelocity, MathHelper.Clamp(velocityGain * stepSeconds, 0.0, 1.0));
    }
}
