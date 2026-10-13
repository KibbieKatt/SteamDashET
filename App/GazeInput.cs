using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valve.VR;

namespace SteamDashET;

internal static class GazeInput
{
    internal const string ActionSet = "/actions/gaze";
    internal const string Action = "/actions/gaze/in/gaze";

    internal static bool TryDirection(VREyeTrackingData_t gaze, out Vector3 direction)
    {
        direction = default;
        if (!gaze.bActive || !gaze.bValid || !gaze.bTracked) return false;
        Vector3 origin = new(gaze.vGazeOrigin.v0, gaze.vGazeOrigin.v1, gaze.vGazeOrigin.v2);
        Vector3 target = new(gaze.vGazeTarget.v0, gaze.vGazeTarget.v1, gaze.vGazeTarget.v2);
        Vector3 delta = target - origin;
        float length = delta.Length();
        if (!Finite(origin) || !Finite(target) || !float.IsFinite(length) || length < 0.000001f) return false;
        direction = delta / length;
        return true;
    }

    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    internal static HmdMatrix34_t DotTransform(HmdMatrix34_t head, VREyeTrackingData_t gaze, Vector3 direction)
    {
        // Gaze is already in Standing space. Use the HMD basis only to orient the flat dot.
        head.m3 = gaze.vGazeOrigin.v0 + direction.X;
        head.m7 = gaze.vGazeOrigin.v1 + direction.Y;
        head.m11 = gaze.vGazeOrigin.v2 + direction.Z;
        return head;
    }

    internal static void CheckAbi()
    {
        if (IntPtr.Size != 8 || Marshal.SizeOf<VREyeTrackingData_t>() != 28 ||
            Marshal.OffsetOf<VREyeTrackingData_t>(nameof(VREyeTrackingData_t.vGazeOrigin)).ToInt32() != 4 ||
            Marshal.OffsetOf<VREyeTrackingData_t>(nameof(VREyeTrackingData_t.vGazeTarget)).ToInt32() != 16 ||
            Marshal.SizeOf<VRActiveActionSet_t>() != 32)
            throw new InvalidOperationException("The OpenVR bindings require a Windows x64 process.");
    }

    internal static string WriteManifest(string directory, string controllerType)
    {
        Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        var types = new[] { "generic_hmd", controllerType }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToArray();
        var bindings = new List<object>();
        for (int i = 0; i < types.Length; i++)
        {
            string filename = $"binding-{i}.json";
            var binding = new
            {
                action_manifest_version = 0, name = "SteamDashET", description = "Dashboard eye gaze",
                controller_type = types[i], bindings = new Dictionary<string, object>
                {
                    [ActionSet] = new { eyetracking = new[] { new { path = "/user/head/eyetracking", output = Action } } }
                }
            };
            File.WriteAllText(Path.Combine(directory, filename), JsonSerializer.Serialize(binding, json));
            bindings.Add(new { controller_type = types[i], binding_url = filename });
        }
        var manifest = new
        {
            action_sets = new[] { new { name = ActionSet, usage = "single" } },
            actions = new[] { new { name = Action, type = "eyetracking", requirement = "optional" } },
            default_bindings = bindings,
            localization = new[] { new Dictionary<string, string>
            {
                ["language_tag"] = "en_US", [ActionSet] = "SteamDashET", [Action] = "Eye gaze"
            } }
        };
        string path = Path.Combine(directory, "actions.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, json));
        return path;
    }
}
