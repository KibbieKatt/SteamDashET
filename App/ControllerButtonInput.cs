using System.Diagnostics;
using System.Text;
using Valve.VR;

namespace SteamDashET;

// The render-model components expose button levels even when dashboard focus suppresses action input.
internal sealed class ControllerButtonInput(CVRSystem system, CVRInput input, CVRRenderModels models,
    Func<double> seconds = null)
{
    private readonly Func<double> clock = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private readonly Hand left = new(ETrackedControllerRole.LeftHand, "/user/hand/left");
    private readonly Hand right = new(ETrackedControllerRole.RightHand, "/user/hand/right");

    internal ControllerButtonFrame Read() => new(ReadHand(left), ReadHand(right));

    // Device events invalidate that controller's metadata and edge identity, including same-index reconnects.
    internal void Invalidate(uint deviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid)
    {
        if (deviceIndex == OpenVR.k_unTrackedDeviceIndexInvalid || left.DeviceIndex == deviceIndex)
            left.Invalidate();
        if (deviceIndex == OpenVR.k_unTrackedDeviceIndexInvalid || right.DeviceIndex == deviceIndex)
            right.Invalidate();
    }

    private ControllerHandButtons ReadHand(Hand hand)
    {
        try
        {
            if (system == null || input == null || models == null)
                return Snapshot(hand);
            uint index = system.GetTrackedDeviceIndexForControllerRole(hand.Role);
            bool connected = index < OpenVR.k_unMaxTrackedDeviceCount && system.IsTrackedDeviceConnected(index);
            if (index != hand.DeviceIndex || connected != hand.Connected)
            {
                hand.Invalidate();
                hand.DeviceIndex = index;
                hand.Connected = connected;
            }
            if (!connected) return Snapshot(hand);

            double now = clock();
            if (hand.Metadata == null || now >= hand.RefreshAt)
            {
                Refresh(hand);
                hand.RefreshAt = now + 1;
            }
            var metadata = hand.Metadata;
            if (!metadata.Supported) return Snapshot(hand);
            if (hand.Source == OpenVR.k_ulInvalidInputValueHandle)
            {
                ulong source = OpenVR.k_ulInvalidInputValueHandle;
                var error = input.GetInputSourceHandle(hand.Path, ref source);
                if (error != EVRInputError.None || source == OpenVR.k_ulInvalidInputValueHandle)
                    return Snapshot(hand);
                hand.Source = source;
            }
            return Snapshot(hand, ReadButton(hand, metadata.A), ReadButton(hand, metadata.B));
        }
        catch
        {
            // A controller-model failure must not tear down gaze tracking or retain a pressed level.
            hand.Invalidate();
            return Snapshot(hand);
        }
    }

    private void Refresh(Hand hand)
    {
        string type = ReadProperty(hand.DeviceIndex, ETrackedDeviceProperty.Prop_ControllerType_String) ?? "";
        string model = ReadProperty(hand.DeviceIndex, ETrackedDeviceProperty.Prop_RenderModelName_String) ?? "";
        string serial = ReadProperty(hand.DeviceIndex, ETrackedDeviceProperty.Prop_SerialNumber_String) ?? "";
        var previous = hand.Metadata;
        if (previous != null && (previous.Type != type || previous.Model != model || previous.Serial != serial))
        {
            hand.Generation++;
            hand.Source = OpenVR.k_ulInvalidInputValueHandle;
        }
        var (a, b) = Components(type, model, hand.Role);
        bool supported = !string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(model) && a != null;
        var available = new HashSet<string>(StringComparer.Ordinal);
        if (supported)
        {
            uint count = models.GetComponentCount(model);
            if (count <= 256)
                for (uint i = 0; i < count; i++)
                {
                    var name = new StringBuilder(1024);
                    uint required = models.GetComponentName(model, i, name, (uint)name.Capacity);
                    if (required > 0 && required <= name.Capacity && name.Length > 0)
                        available.Add(name.ToString());
                }
        }
        hand.Metadata = new Metadata(type, model, serial, a, b, supported, available);
    }

    private ControllerButtonState ReadButton(Hand hand, string component)
    {
        if (!hand.Metadata.Available.Contains(component))
            return default;
        RenderModel_ControllerMode_State_t mode = default;
        RenderModel_ComponentState_t state = default;
        if (!models.GetComponentStateForDevicePath(hand.Metadata.Model, component, hand.Source, ref mode, ref state))
            return default;
        return new ControllerButtonState(true, (state.uProperties & (uint)EVRComponentProperty.IsPressed) != 0);
    }

    private string ReadProperty(uint index, ETrackedDeviceProperty property)
    {
        var text = new StringBuilder(1024);
        var error = ETrackedPropertyError.TrackedProp_Success;
        uint required = system.GetStringTrackedDeviceProperty(index, property, text, (uint)text.Capacity, ref error);
        return error == ETrackedPropertyError.TrackedProp_Success && required <= text.Capacity
            ? text.ToString() : null;
    }

    private static (string A, string B) Components(string type, string model, ETrackedControllerRole role)
    {
        if (type == "knuckles") return ("button_a", "button_b");
        // Installed Touch model definitions identify these components with /input/a/click and /input/b/click.
        // Left Touch has X/Y buttons, so it does not provide the requested A/B inputs.
        if (type == "oculus_touch" && role == ETrackedControllerRole.RightHand)
        {
            if (model.StartsWith("{oculus}", StringComparison.Ordinal)) model = model[8..];
            return model switch
            {
                "oculus_cv1_controller_right" => ("a_button", "b_button"),
                "oculus_rifts_controller_right" or "oculus_quest_controller_right" or
                    "oculus_quest2_controller_right" or "oculus_quest_pro_controller_right" or
                    "oculus_quest_plus_controller_right" => ("button_a", "button_b"),
                _ => (null, null)
            };
        }
        return (null, null);
    }

    private static ControllerHandButtons Snapshot(Hand hand) => Snapshot(hand, default, default);

    private static ControllerHandButtons Snapshot(Hand hand, ControllerButtonState a, ControllerButtonState b) =>
        new(new ControllerHandIdentity(hand.Role, hand.DeviceIndex, hand.Metadata?.Type ?? "",
            hand.Metadata?.Model ?? "", hand.Metadata?.Serial ?? "", hand.Source, hand.Generation), a, b);

    private sealed class Hand(ETrackedControllerRole role, string path)
    {
        internal ETrackedControllerRole Role { get; } = role;
        internal string Path { get; } = path;
        internal uint DeviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
        internal bool Connected;
        internal ulong Source;
        internal long Generation;
        internal double RefreshAt;
        internal Metadata Metadata;

        internal void Invalidate()
        {
            Generation++;
            Metadata = null;
            Source = OpenVR.k_ulInvalidInputValueHandle;
        }
    }

    private sealed record Metadata(string Type, string Model, string Serial, string A, string B,
        bool Supported, HashSet<string> Available);
}

internal sealed record ControllerButtonFrame(ControllerHandButtons Left, ControllerHandButtons Right);
internal sealed record ControllerHandButtons(ControllerHandIdentity Identity, ControllerButtonState A,
    ControllerButtonState B);
internal readonly record struct ControllerHandIdentity(ETrackedControllerRole Role, uint DeviceIndex,
    string ControllerType, string RenderModel, string SerialNumber, ulong InputSourceHandle, long ConnectionGeneration);
internal readonly record struct ControllerButtonState(bool Available, bool Pressed);
