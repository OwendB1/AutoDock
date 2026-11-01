using System;
using VRageMath;

namespace ClientPlugin;

internal readonly struct LinearThrustControl
{
    private readonly Vector3D positiveAcceleration;
    private readonly Vector3D negativeAcceleration;
    private readonly Vector3D localGravity;
    private readonly MatrixD inverseGridMatrix;

    public LinearThrustControl(
        double mass,
        Vector3D positiveThrust,
        Vector3D negativeThrust,
        Vector3D gravity,
        MatrixD inverseGridMatrix)
    {
        this.inverseGridMatrix = inverseGridMatrix;
        positiveAcceleration = new Vector3D(
            GetAcceleration(positiveThrust.X, mass),
            GetAcceleration(positiveThrust.Y, mass),
            GetAcceleration(positiveThrust.Z, mass));
        negativeAcceleration = new Vector3D(
            GetAcceleration(negativeThrust.X, mass),
            GetAcceleration(negativeThrust.Y, mass),
            GetAcceleration(negativeThrust.Z, mass));
        localGravity = mass > 0.0 && !double.IsInfinity(mass)
            ? Vector3D.TransformNormal(gravity, inverseGridMatrix)
            : Vector3D.Zero;
    }

    public double GetAvailableAcceleration(Vector3D worldDirection, double accelerationLimit)
    {
        Vector3D direction = Vector3D.TransformNormal(worldDirection, inverseGridMatrix);
        double available = accelerationLimit;
        available = LimitAcceleration(available, direction.X, positiveAcceleration.X + localGravity.X, negativeAcceleration.X - localGravity.X);
        available = LimitAcceleration(available, direction.Y, positiveAcceleration.Y + localGravity.Y, negativeAcceleration.Y - localGravity.Y);
        available = LimitAcceleration(available, direction.Z, positiveAcceleration.Z + localGravity.Z, negativeAcceleration.Z - localGravity.Z);
        return Math.Max(0.0, available);
    }

    public Vector3 CreateMoveIndicator(Vector3D desiredWorldAcceleration, MatrixD inverseControllerMatrix)
    {
        Vector3D acceleration = Vector3D.TransformNormal(desiredWorldAcceleration, inverseGridMatrix) - localGravity;
        Vector3D gridInput = new Vector3D(
            GetInput(acceleration.X, positiveAcceleration.X, negativeAcceleration.X),
            GetInput(acceleration.Y, positiveAcceleration.Y, negativeAcceleration.Y),
            GetInput(acceleration.Z, positiveAcceleration.Z, negativeAcceleration.Z));
        Vector3D worldInput = Vector3D.TransformNormal(gridInput, MatrixD.Transpose(inverseGridMatrix));
        return (Vector3)Vector3D.TransformNormal(worldInput, inverseControllerMatrix);
    }

    private static double GetAcceleration(double force, double mass)
    {
        return mass > 0.0 && !double.IsInfinity(mass) && force > 0.0 && !double.IsInfinity(force)
            ? force / mass
            : 0.0;
    }

    private static double LimitAcceleration(double available, double direction, double positive, double negative)
    {
        if (Math.Abs(direction) < 1e-6)
            return available;
        if (direction > 0.0)
            return Math.Min(available, positive / direction);
        if (direction < 0.0)
            return Math.Min(available, negative / -direction);
        return available;
    }

    private static double GetInput(double acceleration, double positive, double negative)
    {
        double available = acceleration >= 0.0 ? positive : negative;
        return available > 0.0 ? MathHelper.Clamp(acceleration / available, -1.0, 1.0) : 0.0;
    }
}
