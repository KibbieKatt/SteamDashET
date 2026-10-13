using System.Numerics;

namespace SteamDashET;

// Fit angular residuals at measured gaze centers, not at the displayed target centers.
// Each target contributes one row regardless of how long it was recorded.
internal sealed class RbfCalibrationField : ICalibrationCorrectionField
{
    private const double LengthScale = 6 * Math.PI / 180;
    private const double FitTolerance = 1e-7;
    private readonly Vector2[] centers;
    private readonly double[] yawWeights;
    private readonly double[] pitchWeights;
    private readonly Vector2 meanOffset;
    private readonly float maximumResidual;
    private readonly float maximumOffset;

    internal RbfCalibrationField(CalibrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var unique = new Dictionary<(float Yaw, float Pitch), CalibrationPoint>();
        foreach (var saved in profile.Targets)
        {
            if (saved is not CalibrationPoint point) continue;
            // Identical inputs cannot have different exact outputs. Match the triangle
            // field's deterministic rule: the highest target index wins.
            unique[(point.Yaw, point.Pitch)] = point;
        }
        var observations = unique.Values.OrderBy(point => point.Yaw).ThenBy(point => point.Pitch).ToArray();
        centers = observations.Select(value => new Vector2(value.Yaw, value.Pitch)).ToArray();
        yawWeights = new double[centers.Length];
        pitchWeights = new double[centers.Length];
        if (centers.Length == 0) return;

        var offsets = observations.Select(value => new Vector2(value.TargetYaw - value.Yaw, value.TargetPitch - value.Pitch)).ToArray();
        meanOffset = offsets.Aggregate(Vector2.Zero, (sum, value) => sum + value) / offsets.Length;
        maximumOffset = offsets.Max(value => value.Length());
        maximumResidual = offsets.Max(value => (value - meanOffset).Length());
        if (centers.Length == 1 || maximumResidual == 0) return;

        var matrix = new double[centers.Length, centers.Length];
        for (int row = 0; row < centers.Length; row++)
        {
            yawWeights[row] = offsets[row].X - meanOffset.X;
            pitchWeights[row] = offsets[row].Y - meanOffset.Y;
            for (int column = 0; column <= row; column++)
                matrix[row, column] = matrix[column, row] = Kernel(centers[row], centers[column]);
        }
        // Distinct centers give a positive definite Gaussian matrix, even when collinear.
        // Do not add diagonal regularization or conceal an unresolvable fit with a pivot floor.
        var lower = Cholesky(matrix);
        Solve(lower, yawWeights);
        Solve(lower, pitchWeights);
        ValidateFit(matrix, offsets);
    }

    public Vector3 Correct(Vector3 headsetDirection)
    {
        if (centers.Length == 0 || !CalibrationGrid.TryAngles(headsetDirection, out var measured))
            return headsetDirection;
        double yaw = 0, pitch = 0, yawError = 0, pitchError = 0;
        for (int i = 0; i < centers.Length; i++)
        {
            double kernel = Kernel(measured, centers[i]);
            AddCompensated(ref yaw, ref yawError, kernel * yawWeights[i]);
            AddCompensated(ref pitch, ref pitchError, kernel * pitchWeights[i]);
        }
        // Local detail fades to the mean offset far from recorded points. Limit interpolation
        // overshoot to the correction magnitudes actually demonstrated by the calibration.
        Vector2 residual = Limit(new Vector2((float)yaw, (float)pitch), maximumResidual);
        Vector2 correction = Limit(meanOffset + residual, maximumOffset);
        Vector2 corrected = measured + correction;
        corrected.X = Math.Clamp(corrected.X, -89 * CalibrationGrid.Degrees, 89 * CalibrationGrid.Degrees);
        corrected.Y = Math.Clamp(corrected.Y, -89 * CalibrationGrid.Degrees, 89 * CalibrationGrid.Degrees);
        return CalibrationGrid.FromAngles(corrected);
    }

    private static Vector2 Limit(Vector2 value, float maximum)
    {
        float length = value.Length();
        return length > maximum && length > 0 ? value * (maximum / length) : value;
    }

    private static double Kernel(Vector2 first, Vector2 second)
    {
        double yaw = (double)first.X - second.X, pitch = (double)first.Y - second.Y;
        return Math.Exp(-(yaw * yaw + pitch * pitch) / (2 * LengthScale * LengthScale));
    }

    private static double[,] Cholesky(double[,] matrix)
    {
        int count = matrix.GetLength(0);
        var lower = new double[count, count];
        for (int row = 0; row < count; row++)
            for (int column = 0; column <= row; column++)
            {
                double product = 0, roundingError = 0;
                for (int k = 0; k < column; k++)
                    AddCompensated(ref product, ref roundingError, lower[row, k] * lower[column, k]);
                double value = matrix[row, column] - product;
                if (!double.IsFinite(value) || row == column && value <= 0) throw UnstableFit();
                lower[row, column] = row == column ? Math.Sqrt(value) : value / lower[column, column];
            }
        return lower;
    }

    private static void Solve(double[,] lower, double[] values)
    {
        for (int row = 0; row < values.Length; row++)
        {
            double product = 0, roundingError = 0;
            for (int column = 0; column < row; column++)
                AddCompensated(ref product, ref roundingError, lower[row, column] * values[column]);
            values[row] = (values[row] - product) / lower[row, row];
        }
        for (int row = values.Length - 1; row >= 0; row--)
        {
            double product = 0, roundingError = 0;
            for (int column = row + 1; column < values.Length; column++)
                AddCompensated(ref product, ref roundingError, lower[column, row] * values[column]);
            values[row] = (values[row] - product) / lower[row, row];
            if (!double.IsFinite(values[row])) throw UnstableFit();
        }
    }

    private void ValidateFit(double[,] matrix, Vector2[] offsets)
    {
        // This checks the actual zero-regularization constraints, rather than accepting
        // finite coefficients from a numerically singular matrix as a usable field.
        for (int row = 0; row < centers.Length; row++)
        {
            double yaw = 0, pitch = 0, yawError = 0, pitchError = 0;
            for (int column = 0; column < centers.Length; column++)
            {
                AddCompensated(ref yaw, ref yawError, matrix[row, column] * yawWeights[column]);
                AddCompensated(ref pitch, ref pitchError, matrix[row, column] * pitchWeights[column]);
            }
            if (!double.IsFinite(yaw) || !double.IsFinite(pitch) ||
                Math.Abs(yaw - (offsets[row].X - meanOffset.X)) > FitTolerance ||
                Math.Abs(pitch - (offsets[row].Y - meanOffset.Y)) > FitTolerance) throw UnstableFit();
        }
    }

    private static void AddCompensated(ref double sum, ref double error, double value)
    {
        double adjusted = value - error;
        double next = sum + adjusted;
        error = (next - sum) - adjusted;
        sum = next;
    }

    private static InvalidOperationException UnstableFit() =>
        new("RBF fit is unstable. Recapture targets with nearly identical gaze readings.");
}
