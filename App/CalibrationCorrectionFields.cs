using System.Numerics;

namespace SteamDashET;

internal enum CalibrationInterpolationMethod
{
    SmoothRbf,
    LinearTriangles
}

internal interface ICalibrationCorrectionField
{
    Vector3 Correct(Vector3 direction);
}

internal static class CalibrationCorrectionFields
{
    internal static ICalibrationCorrectionField Create(CalibrationProfile profile, CalibrationInterpolationMethod method) => method switch
    {
        CalibrationInterpolationMethod.SmoothRbf => new RbfCalibrationField(profile),
        CalibrationInterpolationMethod.LinearTriangles => new LinearTriangleCalibrationField(profile),
        _ => throw new ArgumentOutOfRangeException(nameof(method), "Choose a supported calibration interpolation method.")
    };
}
