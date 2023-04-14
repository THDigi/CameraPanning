using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace Digi.CameraPanning
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_CameraBlock), useEntityUpdate: false)]
    public class CameraBlock : MyGameLogicComponent
    {
        MyCubeBlock block;
        bool controlling = false;
        bool recenter = false;

        Matrix OriginalMatrix;
        Matrix RotatedMatrix;
        Vector3 CustomOffset;
        Vector3 OriginalWorldOffsetAsLocal;

        float currentPitch = 0;
        float currentYaw = 0;
        float currentRoll = 0;
        float currentSpeed = 0;

        int soundRotateStopDelay = 0;
        int soundZoomStopDelay = 0;
        MyEntity3DSoundEmitter soundRotateEmitter = null;
        MyEntity3DSoundEmitter soundZoomEmitter = null;

        int prevFOV = 0;
        int ignoreFovChangeForTicks = 0;

        float zoomWidth;
        ZoomLimits zoomLimits;
        MyCameraBlockDefinition blockDef;

        IMyHudNotification Notification => CameraPanningMod.Instance.Notification;

        const float SPEED_MUL = 0.1f; // rotation input multiplier as it is too fast raw compared to the rest of the game
        const float MAX_SPEED = 2f; // using a max speed to feel like it's on actual servos
        const float ZOOM_DISTANCE = 1; // just for math to make sense, don't edit
        const int SOUND_ZOOM_STOP_DELAY = 30; // game ticks
        const float SOUND_ROTATE_VOLUME = 1.0f;
        const float SOUND_ZOOM_VOLUME = 0.35f;
        static readonly MySoundPair SOUND_ROTATE_PAIR = new MySoundPair("ArcBlockRotor");
        static readonly MySoundPair SOUND_ZOOM_PAIR = new MySoundPair("ArcWepShipGatlingRotation");
        const float EPSILON = 0.0001f;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            if(MyAPIGateway.Utilities.IsDedicated)
                return; // DS doesn't need any of this

            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                if(MyAPIGateway.Utilities.IsDedicated)
                    return; // DS doesn't need any of this

                block = (MyCubeBlock)Entity;

                if(block.IsPreview || block.CubeGrid.IsPreview || block.CubeGrid.Physics == null) // ignore ghost grids
                {
                    block = null;
                    return;
                }

                blockDef = (MyCameraBlockDefinition)block.BlockDefinition;

                if(!CameraPanningMod.Instance.WidthLimits.TryGetValue(blockDef.Id, out zoomLimits))
                {
                    Log.Error($"{blockDef.Id.ToString()} didn't exist at BeforeStart() time so it has no stored limits! What is going on?!", Log.PRINT_MESSAGE);
                    return;
                }

                NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME;

                zoomWidth = FovToWidth(MathHelper.ToRadians(60));

                OriginalMatrix = Entity.LocalMatrix;
                RotatedMatrix = OriginalMatrix;

                Vector3D worldViewPosition = MatrixD.Invert(block.GetViewMatrix()).Translation;
                OriginalWorldOffsetAsLocal = Vector3D.Transform(worldViewPosition, MatrixD.Invert(block.WorldMatrix)); // to local

                // HACK temporary fix for camera being in the center of the block
                TryFixCameraPosition(); // moves the camera view's position towards the default mount point by gridSize/2.

                if(soundRotateEmitter == null)
                {
                    soundRotateEmitter = new MyEntity3DSoundEmitter((MyEntity)Entity);
                    soundRotateEmitter.CustomVolume = SOUND_ROTATE_VOLUME;
                }

                if(soundZoomEmitter == null)
                {
                    soundZoomEmitter = new MyEntity3DSoundEmitter((MyEntity)Entity);
                    soundZoomEmitter.CustomVolume = SOUND_ZOOM_VOLUME;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void Close()
        {
            try
            {
                if(soundRotateEmitter != null)
                    soundRotateEmitter.StopSound(true, true);

                if(soundZoomEmitter != null)
                    soundZoomEmitter.StopSound(true, true);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public bool IsValid => (block != null && block.CubeGrid.Physics.Enabled);

        public void ZoomIn()
        {
            if(!IsValid)
                return;

            zoomWidth *= 0.9f;
            UpdateZoom();
        }

        public void ZoomOut()
        {
            if(!IsValid)
                return;

            zoomWidth *= 1.1f;
            UpdateZoom();
        }

        void UpdateZoom()
        {
            zoomWidth = MathHelper.Clamp(zoomWidth, zoomLimits.MinWidth, zoomLimits.MaxWidth);

            float fov = WidthToFov(zoomWidth);
            blockDef.MinFov = fov;
            blockDef.MaxFov = fov;
        }

        public static float FovToWidth(float fovRadians) => 2 * (float)Math.Tan(fovRadians / 2d) * ZOOM_DISTANCE;
        public static float WidthToFov(float width) => 2 * (float)Math.Atan(width / 2 / ZOOM_DISTANCE);

        public override void UpdateAfterSimulation()
        {
            try
            {
                if(!IsValid)
                    return;

                bool rotating = Update(); // returns true if the camera is rotating, false otherwise

                if(soundRotateEmitter != null)
                {
                    if(rotating && soundRotateStopDelay > 0) // reset the stop delay
                        soundRotateStopDelay = 0;

                    if(soundRotateEmitter.IsPlaying) // dynamically adjust sound volume depending on last rotation speed
                        soundRotateEmitter.CustomVolume = 0.2f + (MathHelper.Clamp(currentSpeed / MAX_SPEED, 0, 1) * 0.6f);

                    if(!rotating && soundRotateEmitter != null && soundRotateEmitter.IsPlaying)
                    {
                        if(soundRotateStopDelay == 0)
                            soundRotateStopDelay = SOUND_ZOOM_STOP_DELAY;

                        if(--soundRotateStopDelay == 0)
                            soundRotateEmitter.StopSound(true);
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        bool Update()
        {
            if(MyAPIGateway.Session.CameraController != Entity)
            {
                if(controlling) // if it was being controlled, restore the local matrix, stop sounds, etc
                {
                    controlling = false;
                    Notification.Hide();

                    //Entity.Render.Visible = true; // restore camera model
                    Entity.SetLocalMatrix(OriginalMatrix); // reset the camera's matrix to avoid seeing its model skewed if the model gets updated with the local matrix
                    Entity.Render.UpdateRenderObject(true); // force model to be recalculated to avoid invisible models on merge/unmerge while camera is viewed

                    // reset definition on exit camera for allowing mods to read proper values
                    blockDef.MinFov = zoomLimits.MinFov;
                    blockDef.MaxFov = zoomLimits.MaxFov;

                    if(soundZoomEmitter != null)
                        soundZoomEmitter.StopSound(true);
                }

                return false; // not controlled, no need to update further
            }

            var lookaroundControl = MyAPIGateway.Input.GetGameControl(MyControlsSpace.LOOKAROUND);
            var rotationTypeControl = MyAPIGateway.Input.GetGameControl(MyControlsSpace.SPRINT);
            var cameraModeControl = MyAPIGateway.Input.GetGameControl(MyControlsSpace.CAMERA_MODE);
            float FovRad = MyAPIGateway.Session.Camera.FovWithZoom;
            int FOV = (int)Math.Round(MathHelper.ToDegrees(FovRad), 0);

            // takes a few ticks for view to change to camera...
            if(ignoreFovChangeForTicks > 0)
            {
                ignoreFovChangeForTicks--;
                prevFOV = FOV;
                zoomWidth = FovToWidth(FovRad);
            }

            if(!controlling) // just taken control of this camera
            {
                controlling = true; // only show this message once per camera control

                // hide the camera model to avoid weirdness...
                // but disabled to allow mods to model things in view with their camera model.
                //Entity.Render.Visible = false;

                OriginalMatrix = Entity.LocalMatrix; // recalculate original matrix and rotated matrix in case the block was "moved" (by merge or who knows what else)
                RotateCamera(0, 0, 0, true);

                Entity.SetLocalMatrix(RotatedMatrix); // restore the last view matrix

                prevFOV = FOV;
                zoomWidth = FovToWidth(FovRad);
                ignoreFovChangeForTicks = 2;

                string lookaround = GetControlAssignedName(lookaroundControl);
                string rotationType = GetControlAssignedName(rotationTypeControl);
                string cameraMode = GetControlAssignedName(cameraModeControl);
                string text = $"Hold [{lookaround}] to pan camera, [{lookaround}]+[{rotationType}] to change rotation type and [{cameraMode}] to reset.";

                Notify(text, 3000);
            }
            else
            {
                if(Math.Abs(FOV - prevFOV) > 0)
                {
                    prevFOV = FOV;

                    if(soundZoomEmitter != null && !soundZoomEmitter.IsPlaying)
                    {
                        soundZoomEmitter.PlaySound(SOUND_ZOOM_PAIR, stopPrevious: true, skipIntro: true, alwaysHearOnRealistic: true, force2D: true);
                        soundZoomStopDelay = SOUND_ZOOM_STOP_DELAY;
                    }

                    Notify(FOV.ToString("0°"), 300);
                }
                else
                {
                    if(soundZoomEmitter != null && soundZoomEmitter.IsPlaying)
                    {
                        if(soundZoomStopDelay == 0 || --soundZoomStopDelay == 0)
                            soundZoomEmitter.StopSound(true);
                    }
                }
            }

            if(!MyAPIGateway.Gui.ChatEntryVisible && !MyAPIGateway.Gui.IsCursorVisible)
            {
                if(!recenter && cameraModeControl.IsNewPressed())
                {
                    recenter = true;
                    return false; // no reason to compute further
                }

                if(lookaroundControl.IsPressed())
                {
                    if(Notification.Text.Length > 10) // if it's the control hints, hide it
                        Notification.Hide();

                    var rot = MyAPIGateway.Input.GetRotation();
                    bool rollToggle = rotationTypeControl.IsPressed();

                    if(Math.Abs(rot.X) > EPSILON || Math.Abs(rot.Y) > EPSILON)
                    {
                        if(recenter)
                            recenter = false;

                        if(rollToggle)
                        {
                            // slowly reset yaw while giving control to roll
                            if(RotateCamera(rot.X * SPEED_MUL, MathHelper.Lerp(currentYaw, 0f, 0.1f), rot.Y * SPEED_MUL))
                                return true;
                        }
                        else
                        {
                            if(RotateCamera(rot.X * SPEED_MUL, rot.Y * SPEED_MUL, 0))
                                return true;
                        }
                    }
                    else if(rollToggle && Math.Abs(currentYaw) > EPSILON) // not moving mouse but holding modifier should result in yaw being slowly reset to 0
                    {
                        if(RotateCamera(0, MathHelper.Lerp(currentYaw, 0f, 0.1f), 0))
                            return true;
                    }
                }
            }

            if(recenter)
            {
                if(Math.Abs(currentPitch) > EPSILON || Math.Abs(currentYaw) > EPSILON || Math.Abs(currentRoll) > EPSILON)
                {
                    if(RotateCamera(MathHelper.Lerp(currentPitch, 0f, 0.1f), MathHelper.Lerp(currentYaw, 0f, 0.1f), MathHelper.Lerp(currentRoll, 0f, 0.1f)))
                        return true;
                }
                else
                {
                    recenter = false;

                    // ensure it's perfectly centered
                    currentPitch = 0;
                    currentYaw = 0;
                    currentRoll = 0;
                    Entity.SetLocalMatrix(RotatedMatrix);
                    return true;
                }
            }

            return false;
        }

        void Notify(string text, int aliveTimeMs)
        {
            var notification = CameraPanningMod.Instance.Notification;

            if(notification == null)
                notification = CameraPanningMod.Instance.Notification = MyAPIGateway.Utilities.CreateNotification("");

            notification.Hide(); // required since SE v1.194
            notification.AliveTime = aliveTimeMs;
            notification.Text = text;
            notification.Show();
        }

        float ClampAngle(float value, float limit = 0)
        {
            if(limit > 0)
                value = MathHelper.Clamp(value, -limit, limit);

            if(value > 180)
                value = -180 + (value - 180);
            else if(value < -180)
                value = 180 - (value - 180);

            return value;
        }

        float ClampMaxSpeed(float value)
        {
            return MathHelper.Clamp(value, -MAX_SPEED, MAX_SPEED);
        }

        bool RotateCamera(float pitchMod, float yawMod, float rollMod, bool forceRecalculate = false)
        {
            IMyCameraBlock camera = (IMyCameraBlock)Entity;
            float angleLimit = camera.RaycastConeLimit;

            pitchMod = ClampMaxSpeed(pitchMod);
            yawMod = ClampMaxSpeed(yawMod);
            rollMod = ClampMaxSpeed(rollMod);

            float setPitch = ClampAngle(currentPitch - pitchMod, angleLimit);
            float setYaw = ClampAngle(currentYaw - yawMod, angleLimit);
            float setRoll = ClampAngle(currentRoll - rollMod);

            if(forceRecalculate || Math.Abs(setPitch - currentPitch) >= EPSILON || Math.Abs(setYaw - currentYaw) >= EPSILON || Math.Abs(setRoll - currentRoll) >= EPSILON)
            {
                currentSpeed = new Vector3(pitchMod, yawMod, rollMod).Length();
                currentPitch = setPitch;
                currentYaw = setYaw;
                currentRoll = setRoll;

                Matrix roll = MatrixD.CreateFromYawPitchRoll(0, 0, MathHelper.ToRadians(currentRoll));
                Matrix yawAndPitch = MatrixD.CreateFromYawPitchRoll(MathHelper.ToRadians(currentYaw), MathHelper.ToRadians(currentPitch), 0);

                Matrix rotated = yawAndPitch * roll * OriginalMatrix;

                // counter-shift the position so that it stays off-center where it needs to be
                Vector3 rotatedOffset = Vector3.TransformNormal(OriginalWorldOffsetAsLocal, rotated);
                Vector3 originalOffset = Vector3.TransformNormal(OriginalWorldOffsetAsLocal, OriginalMatrix);
                Vector3 dir = originalOffset - rotatedOffset;

                rotated.Translation += dir;

                rotated.Translation += CustomOffset;

                RotatedMatrix = rotated;
                Entity.SetLocalMatrix(rotated);

                if(soundRotateEmitter != null && !soundRotateEmitter.IsPlaying)
                    soundRotateEmitter.PlaySound(SOUND_ROTATE_PAIR, stopPrevious: true, skipIntro: true, alwaysHearOnRealistic: true, force2D: true);

                return true;
            }

            return false;
        }

        void TryFixCameraPosition()
        {
            // ignore mods and blocks that use ModelOffset
            if(blockDef?.Context == null || !blockDef.Context.IsBaseGame || blockDef.ModelOffset.LengthSquared() > EPSILON)
                return;

            IMyCubeBlock b = (IMyCubeBlock)block;
            Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();
            b.Model.GetDummies(dummies);

            foreach(IMyModelDummy dummy in dummies.Values)
            {
                // MyCameraBlock.GetViewMatrix()
                if(dummy.Name == MyCameraBlock.DUMMY_NAME_POSITION)
                {
                    // has custom position, don't offset it.
                    return;
                }
            }

            CustomOffset = OriginalMatrix.Backward * ((block.CubeGrid.GridSize / 2f) - 0.05f);
            RotatedMatrix.Translation += CustomOffset;
        }

        static string GetControlAssignedName(IMyControl control)
        {
            string assign = control.GetControlButtonName(MyGuiInputDeviceEnum.Mouse);

            if(!string.IsNullOrWhiteSpace(assign))
                return assign;

            assign = control.GetControlButtonName(MyGuiInputDeviceEnum.Keyboard);

            if(!string.IsNullOrWhiteSpace(assign))
                return assign;

            assign = control.GetControlButtonName(MyGuiInputDeviceEnum.KeyboardSecond);

            if(!string.IsNullOrWhiteSpace(assign))
                return assign;

            return "(unassigned)";
        }
    }
}
