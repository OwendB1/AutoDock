using VRageMath;

namespace ClientPlugin;

public class SavedConnectorAlignment
{
    public long LocalConnectorId { get; set; }
    public long TargetConnectorId { get; set; }
    public double ForwardX { get; set; }
    public double ForwardY { get; set; }
    public double ForwardZ { get; set; }
    public double UpX { get; set; }
    public double UpY { get; set; }
    public double UpZ { get; set; }

    public bool Matches(long localConnectorId, long targetConnectorId)
    {
        return LocalConnectorId == localConnectorId && TargetConnectorId == targetConnectorId;
    }

    public MatrixD GetRelativeConnectorMatrix()
    {
        Vector3D forward = new Vector3D(ForwardX, ForwardY, ForwardZ);
        Vector3D up = new Vector3D(UpX, UpY, UpZ);
        if (forward.LengthSquared() < 0.5 || up.LengthSquared() < 0.5)
            return MatrixD.Identity;

        forward.Normalize();
        up.Normalize();

        MatrixD matrix = MatrixD.CreateWorld(Vector3D.Zero, forward, up);
        matrix.Translation = Vector3D.Zero;
        return matrix;
    }

    public void SetRelativeConnectorMatrix(MatrixD relativeConnectorMatrix)
    {
        Vector3D forward = relativeConnectorMatrix.Forward;
        Vector3D up = relativeConnectorMatrix.Up;
        if (forward.LengthSquared() >= 0.5)
            forward.Normalize();
        if (up.LengthSquared() >= 0.5)
            up.Normalize();

        ForwardX = forward.X;
        ForwardY = forward.Y;
        ForwardZ = forward.Z;
        UpX = up.X;
        UpY = up.Y;
        UpZ = up.Z;
    }
}
