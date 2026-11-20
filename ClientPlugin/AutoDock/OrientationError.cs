using System;
using VRageMath;

namespace ClientPlugin;

internal static class OrientationError
{
    // Below this distance from a half-turn the cross-product sum is too short to give a reliable axis.
    private const double HalfTurnBand = 0.1;

    /// <summary>
    /// World-space rotation error from <paramref name="current"/> to <paramref name="target"/>:
    /// rotation axis scaled by twice the rotation angle.
    /// </summary>
    /// <remarks>
    /// The sum of basis cross products equals 2·sin(angle)·axis. That matches this result for small angles,
    /// so existing PID tuning still applies, but it shrinks back to zero towards 180 degrees, which left
    /// a ship facing away from the target with almost no turning command.
    /// </remarks>
    public static Vector3D Compute(MatrixD current, MatrixD target)
    {
        Vector3D cf = current.Forward, cu = current.Up, cr = current.Right;
        Vector3D tf = target.Forward, tu = target.Up, tr = target.Right;
        Vector3D cross = Vector3D.Cross(cf, tf) + Vector3D.Cross(cu, tu) + Vector3D.Cross(cr, tr);

        double cosine = MathHelper.Clamp((Vector3D.Dot(cf, tf) + Vector3D.Dot(cu, tu) + Vector3D.Dot(cr, tr) - 1.0) * 0.5, -1.0, 1.0);
        double angle = Math.Acos(cosine);
        if (angle < 1e-4)
            return cross;

        double crossLength = cross.Length();
        Vector3D axis;
        if (angle < Math.PI - HalfTurnBand && crossLength > 1e-12)
        {
            // The cross-product direction is exact for every angle below 180 degrees; only its length shrinks.
            axis = cross / crossLength;
        }
        else
        {
            // Near 180 degrees the axis comes from the symmetric part of the rotation: (R + I) / 2 = n·nᵀ.
            axis = Vector3D.Zero;
            double best = -1.0;
            foreach (Vector3D unit in new[] { Vector3D.UnitX, Vector3D.UnitY, Vector3D.UnitZ })
            {
                Vector3D column = tf * Vector3D.Dot(cf, unit) + tu * Vector3D.Dot(cu, unit) + tr * Vector3D.Dot(cr, unit) + unit;
                double lengthSquared = column.LengthSquared();
                if (lengthSquared > best)
                {
                    best = lengthSquared;
                    axis = column;
                }
            }

            if (axis.LengthSquared() < 1e-12)
                return cross;

            axis.Normalize();
            if (crossLength > 1e-9 && Vector3D.Dot(axis, cross) < 0.0)
                axis = -axis;
        }

        return axis * (2.0 * angle);
    }
}
