using System;
using ClientPlugin;
using VRageMath;

internal static partial class Program
{
    private static void CheckOrientationError()
    {
        MatrixD current = MatrixD.CreateFromYawPitchRoll(0.3, 0.2, 0.1);
        Vector3D axis = Vector3D.Normalize(new Vector3D(0.2, 1, 0.3));
        foreach (double degrees in new[] { 0.5, 5.0, 45.0, 90.0, 135.0, 170.0, 179.0, 179.9, 180.0 })
        {
            double angle = MathHelper.ToRadians(degrees);
            MatrixD target = current * MatrixD.CreateFromAxisAngle(axis, angle);
            Vector3D error = OrientationError.Compute(current, target);

            // Magnitude is twice the angle, also for a half-turn where the cross-product sum vanishes.
            AssertOrientation(Math.Abs(error.Length() - 2.0 * angle) < 1e-3,
                $"Orientation error magnitude at {degrees} deg was {error.Length()}");
            if (degrees < 179.95)
                AssertOrientation(Vector3D.Dot(Vector3D.Normalize(error), axis) > 0.999,
                    $"Orientation error axis at {degrees} deg points the wrong way");

            // A small turn along the error must reduce the remaining angle.
            MatrixD stepped = current * MatrixD.CreateFromAxisAngle(Vector3D.Normalize(error), 0.01);
            AssertOrientation(RemainingAngle(stepped, target) < RemainingAngle(current, target),
                $"Turning along the orientation error at {degrees} deg did not reduce the angle");
        }

        // Small angles keep the previous cross-product scale, so PID tuning is unchanged.
        MatrixD small = current * MatrixD.CreateFromAxisAngle(axis, 0.01);
        Vector3D crossSum = Vector3D.Cross(current.Forward, small.Forward)
                            + Vector3D.Cross(current.Up, small.Up)
                            + Vector3D.Cross(current.Right, small.Right);
        CheckVector(OrientationError.Compute(current, small), crossSum);
        CheckVector(OrientationError.Compute(current, current), Vector3D.Zero);
    }

    private static double RemainingAngle(MatrixD a, MatrixD b)
    {
        double cosine = (Vector3D.Dot(a.Forward, b.Forward) + Vector3D.Dot(a.Up, b.Up) + Vector3D.Dot(a.Right, b.Right) - 1.0) * 0.5;
        return Math.Acos(MathHelper.Clamp(cosine, -1.0, 1.0));
    }

    private static void AssertOrientation(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
