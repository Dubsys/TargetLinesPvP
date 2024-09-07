﻿using Dalamud.Game.ClientState.Objects.Types;
using DrahsidLib;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using ImGuiNET;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using static TargetLines.ClassJobHelper;

namespace TargetLines;

internal struct LinePoint {
    public Vector2 Pos;
    public bool Visible;
    public float Dot;

    public LinePoint(Vector2 pos, bool visible, float dot) {
        Pos = pos;
        Visible = visible;
        Dot = dot;
    }
}

public unsafe class TargetLine {
    public enum LineState {
        NewTarget, // new target (from no target)
        Dying, // no target, fading away
        Dying2, // render flags 0x800
        Switching, // switching to different target
        Idle // being targeted
    };

    public IGameObject Self;

    public LineState State = LineState.NewTarget;
    public bool Sleeping = true;
    public TargetSettingsPair LineSettings = new TargetSettingsPair(new TargetSettings(), new TargetSettings(), new LineColor());

    private Vector2 ScreenPos = new Vector2();
    private Vector2 MidScreenPos = new Vector2();
    private Vector2 TargetScreenPos = new Vector2();

    private Vector3 Position = new Vector3();
    private Vector3 MidPosition = new Vector3();

    private Vector3 TargetPosition = new Vector3();
    private Vector3 LastTargetPosition = new Vector3();
    private Vector3 LastTargetPosition2 = new Vector3();

    private RGBA LineColor = new RGBA(0, 0, 0, 0);
    private RGBA OutlineColor = new RGBA(0, 0, 0, 0);
    private RGBA LastLineColor = new RGBA(0, 0, 0, 0);
    private RGBA LastOutlineColor = new RGBA(0, 0, 0, 0);

    private bool HasTarget = false;
    private bool HadTarget = false;
    private ulong LastTargetId = 0;

    private bool DrawBeginCap = false;
    private bool DrawMid = false;
    private bool DrawEndCap = false;

    private float StateTime = 0.0f;
    private float MidHeight = 0.0f;
    private float LastMidHeight = 0.0f;
    private float LastTargetHeight = 0.0f;

    private Stopwatch FPPTransition = new Stopwatch();
    private float FPPLastTransition = 0.0f;
    
    private LinePoint[] Points = new LinePoint[3];
    private int SampleCount = 3;
    private float LinePointStep = 1.0f / (3.0f - 1);

    private const float HPI = MathF.PI * 0.5f;
    private readonly Vector2 uv1 = new Vector2(0, 0);
    private readonly Vector2 uv2 = new Vector2(0, 1.0f);
    private readonly Vector2 uv3 = new Vector2(1.0f, 1.0f);
    private readonly Vector2 uv4 = new Vector2(1.0f, 0);

    public TargetLine() {
        InitializeTargetLine();
    }

    public unsafe void InitializeTargetLine(IGameObject obj = null) {
        if (obj != null) {
            Self = obj;
            if (Self.TargetObject != null && Self.TargetObject.IsValid()) {
                LastTargetId = Self.TargetObject.TargetObjectId;
                LastTargetPosition = Self.TargetObject.Position;
                LastTargetPosition2 = LastTargetPosition;
            }
            else {
                LastTargetPosition = Self.Position;
                LastTargetPosition2 = LastTargetPosition;
            }

            if (Sleeping) {
                State = LineState.NewTarget;
            }

            Sleeping = false;
            InitializeLinePoints();
        }
    }

    public UIRect GetBoundingBox()
    {
        float margin = 0.5f * Globals.Config.saved.LineThickness; // extra room for the texture
        float minX = Math.Min(ScreenPos.X, Math.Min(MidScreenPos.X, TargetScreenPos.X)) - margin;
        float minY = Math.Min(ScreenPos.Y, Math.Min(MidScreenPos.Y, TargetScreenPos.Y)) - margin;
        float maxX = Math.Max(ScreenPos.X, Math.Max(MidScreenPos.X, TargetScreenPos.X)) + margin;
        float maxY = Math.Max(ScreenPos.Y, Math.Max(MidScreenPos.Y, TargetScreenPos.Y)) + margin;

        Vector2 position = new Vector2(minX, minY);
        Vector2 size = new Vector2(maxX - minX, maxY - minY);

        return new UIRect(position, size);
    }

    private void InitializeLinePoints(int sampleCount = 3) {
        if (Points.Length != Globals.Config.saved.TextureCurveSampleCountMax) {
            Points = new LinePoint[Globals.Config.saved.TextureCurveSampleCountMax];
        }

        SampleCount = sampleCount;
        LinePointStep = 1.0f / (float)(SampleCount - 1);
    }

    private void DrawSolidLine() {
        ImDrawListPtr drawlist = ImGui.GetWindowDrawList();
        float outlineThickness = Globals.Config.saved.OutlineThickness;
        float lineThickness = Globals.Config.saved.LineThickness;

        if (LineSettings.LineColor.UseQuad) {
            if (outlineThickness > 0) {
                drawlist.AddBezierQuadratic(ScreenPos, MidScreenPos, TargetScreenPos, OutlineColor.raw, outlineThickness);
            }
            if (lineThickness > 0) {
                drawlist.AddBezierQuadratic(ScreenPos, MidScreenPos, TargetScreenPos, LineColor.raw, lineThickness);
            }
        }
        else {
            if (outlineThickness > 0) {
                drawlist.AddBezierCubic(ScreenPos, MidScreenPos, MidScreenPos, TargetScreenPos, OutlineColor.raw, outlineThickness);
            }
            if (lineThickness > 0) {
                drawlist.AddBezierCubic(ScreenPos, MidScreenPos, MidScreenPos, TargetScreenPos, LineColor.raw, lineThickness);
            }
        }
    }

    private unsafe RGBA[] DrawFancyLine_LineColor(int index) {
        RGBA[] linecolor = new RGBA[2];
        linecolor[0].raw = LineColor.raw;
        linecolor[1].raw = OutlineColor.raw;

        if (Globals.Config.saved.PulsingEffect) {
            float p = index * LinePointStep;
            float max = LineColor.a;
            float min = max * 0.5f;
            float pulsatingAlpha = MathF.Sin(-((float)Globals.Runtime) * Globals.Config.saved.WaveFrequencyScalar + (p * MathF.PI) + HPI);
            float pulsatingAmplitude = (max - min) * (1.0f - Globals.Config.saved.WaveAmplitudeOffset);
            pulsatingAlpha = Math.Clamp(pulsatingAlpha * pulsatingAmplitude + min, min, max);
            linecolor[0].a = (byte)pulsatingAlpha;
            linecolor[1].a = (byte)pulsatingAlpha;
        }

        if (Globals.Config.saved.FadeToEnd) {
            float alphaFade = MathUtils.Lerpf((float)linecolor[1].a,
                (float)(linecolor[1].a * Globals.Config.saved.FadeToEndScalar),
                (float)index * LinePointStep);

            linecolor[1].a = (byte)alphaFade;
        }

        return linecolor;
    }

    private unsafe void DrawFancyLine_Caps(ImDrawListPtr drawlist, float lineThickness, bool firstSegmentOccluded, bool lastSegmentOccluded) {
        Vector2 start_dir = Vector2.Normalize(Points[1].Pos - Points[0].Pos);
        Vector2 end_dir = Vector2.Normalize(Points[SampleCount - 1].Pos - Points[SampleCount - 2].Pos);
        Vector2 start_perp = new Vector2(-start_dir.Y, start_dir.X) * lineThickness;
        Vector2 end_perp = new Vector2(-end_dir.Y, end_dir.X) * lineThickness;

        Vector2 start_p1 = Points[0].Pos - start_dir;
        Vector2 start_p2 = Points[0].Pos + start_dir;

        start_p1.X -= lineThickness * 0.45f;
        start_p1.Y -= lineThickness * 0.45f;
        start_p2.X += lineThickness * 0.45f;
        start_p2.Y += lineThickness * 0.45f;

        Vector2 end_p1 = Points[SampleCount - 1].Pos - end_dir;
        Vector2 end_p2 = Points[SampleCount - 1].Pos + end_dir;

        end_p1.X -= lineThickness * 0.45f;
        end_p1.Y -= lineThickness * 0.45f;
        end_p2.X += lineThickness * 0.45f;
        end_p2.Y += lineThickness * 0.45f;

        RGBA* linecolor_end = stackalloc RGBA[1];
        linecolor_end->raw = LineColor.raw;
        if (Globals.Config.saved.FadeToEnd) {
            linecolor_end->a = (byte)(linecolor_end->a * Globals.Config.saved.FadeToEndScalar);
        }

        if (DrawBeginCap && !firstSegmentOccluded) {
            var wrap = Globals.EdgeTexture.GetWrapOrEmpty();
            if (wrap != null) {
                drawlist.AddImage(wrap.ImGuiHandle, start_p1, start_p2, uv1, uv3, LineColor.raw);
            }
        }

        if (DrawEndCap && !lastSegmentOccluded) {
            var wrap = Globals.EdgeTexture.GetWrapOrEmpty();
            if (wrap != null) {
                drawlist.AddImage(wrap.ImGuiHandle, end_p1, end_p2, uv1, uv3, linecolor_end->raw);
            }
        }
    }

    private unsafe void DrawFancyLine_Segments(ImDrawListPtr drawlist, float lineThickness, float outlineThickness, out bool firstSegmentOccluded, out bool lastSegmentOccluded) {
        bool segmentOccluded;

        firstSegmentOccluded = false;
        lastSegmentOccluded = false;

        for (int index = 0; index < SampleCount - 1; index++) {
            LinePoint point = Points[index];
            LinePoint nextpoint = Points[index + 1];
            if (!point.Visible && !nextpoint.Visible) {
                continue;
            }

            // skip lines that intersect the camera in first person
            if (!Globals.IsAngleThetaInsidePerspective(point.Dot) || !Globals.IsAngleThetaInsidePerspective(nextpoint.Dot)) {
                if (index == 0) {
                    firstSegmentOccluded = true;
                }
                else if (index == SampleCount - 2) {
                    lastSegmentOccluded = true;
                }
                continue;
            }

            Vector2 p1 = point.Pos;
            Vector2 p2 = nextpoint.Pos;

            Vector2 dir = Vector2.Normalize(p2 - p1);
            Vector2 perp = new Vector2(-dir.Y, dir.X) * lineThickness;
            Vector2 perpo = new Vector2(-dir.Y, dir.X) * outlineThickness;

            Vector2 p1_perp = p1 + perp;
            Vector2 p2_perp = p2 + perp;
            Vector2 p1_perp_inv = p1 - perp;
            Vector2 p2_perp_inv = p2 - perp;

            Vector2 p1_perpo = p1 + perpo;
            Vector2 p2_perpo = p2 + perpo;
            Vector2 p1_perp_invo = p1 - perpo;
            Vector2 p2_perp_invo = p2 - perpo;

            RGBA[] linecolor = DrawFancyLine_LineColor(index);

            segmentOccluded = linecolor[0].a == 0 || lineThickness == 0;
            if (index == 0)
            {
                firstSegmentOccluded = segmentOccluded;
            }
            if (index == SampleCount - 2)
            {
                lastSegmentOccluded = segmentOccluded;
            }

            if (!segmentOccluded) {
                var wrapline = Globals.LineTexture.GetWrapOrEmpty();
                drawlist.AddImageQuad(wrapline.ImGuiHandle, p1_perp_inv, p2_perp_inv, p2_perp, p1_perp, uv1, uv2, uv3, uv4, linecolor[0].raw);

                if (linecolor[1].a != 0 && outlineThickness != 0) {
                    var wrapoutline = Globals.OutlineTexture.GetWrapOrEmpty();
                    drawlist.AddImageQuad(wrapoutline.ImGuiHandle, p1_perp_invo, p2_perp_invo, p2_perpo, p1_perpo, uv1, uv2, uv3, uv4, linecolor[1].raw);
                }
            }
        }
    }

    private unsafe void DrawFancyLine() {
        ImDrawListPtr drawlist = ImGui.GetWindowDrawList();

        float lineThickness = Globals.Config.saved.LineThickness * 2.0f;
        float outlineThickness = Globals.Config.saved.OutlineThickness * 2.0f;

        bool firstSegmentOccluded;
        bool lastSegmentOccluded;
        DrawFancyLine_Segments(drawlist, lineThickness, outlineThickness, out firstSegmentOccluded, out lastSegmentOccluded);
        DrawFancyLine_Caps(drawlist, lineThickness, firstSegmentOccluded, lastSegmentOccluded);
    }

    private void UpdateMidPosition() {
        MidPosition = (Position + TargetPosition) * 0.5f;

        if (Self.GetIsPlayerCharacter()) {
            MidPosition.Y += Globals.Config.saved.PlayerHeightBump;
        }
        else if (Self.GetIsBattleChara()) {
            MidPosition.Y += Globals.Config.saved.EnemyHeightBump;
        }

        float heightFix = 0.75f;
        if (LineSettings.LineColor.UseQuad) {
            heightFix = 1.0f;
        }

        if (State == LineState.Dying) {
            float alpha = StateTime / Globals.Config.saved.NoTargetFadeTime;
            heightFix *= 1.0f - alpha;
        }
        else if (State == LineState.NewTarget) {
            float alpha = StateTime / Globals.Config.saved.NewTargetEaseTime;
            heightFix *= alpha;
        }

        MidPosition.Y += (MidHeight * Globals.Config.saved.ArcHeightScalar) * heightFix;
    }

    private unsafe Vector3 GetTransitionPosition(Vector3 startPosition, Vector3 endPosition, float transition, bool isFPP) {
        if (transition == 0.0f) {
            FPPTransition.Reset();
            if (isFPP) {
                return endPosition;
            }
        }
        else {
            if (!FPPTransition.IsRunning || MathF.Sign(transition) != MathF.Sign(FPPLastTransition)) {
                FPPTransition.Restart();
            }
            FPPLastTransition = transition;
        }
    
        float t = (FPPTransition.ElapsedMilliseconds / 1000.0f) / 0.49f;
        if (transition < 0) {
            t *= 0.5f;
        }
        else {
            t *= 2.0f;
        }

        if (t > 1) {
            t = 1;
        }

        return Vector3.Lerp(transition > 0 ? startPosition : endPosition, transition > 0 ? endPosition : startPosition, t);
    }

    private unsafe Vector3 CalculatePosition(Vector3 tppPosition, float height, bool isPlayer, out bool fpp) {
        Vector3 position = tppPosition;
        fpp = false;
        if (isPlayer) {
            fpp = Globals.IsInFirstPerson();
            var cam = Service.CameraManager->Camera;
            float transition = Marshal.PtrToStructure<float>(((IntPtr)cam) + 0x1E0); // TODO: place in struct
            if (fpp || transition != 0 || FPPTransition.IsRunning) {
                Vector3 cameraPosition = Globals.WorldCamera_GetPos() + (-2.0f * Globals.WorldCamera_GetForward());
                cameraPosition.Y -= height;
                position = GetTransitionPosition(tppPosition, cameraPosition, transition, fpp);
            }
        }
        return position;
    }
    
    public unsafe Vector3 GetSourcePosition(out bool fpp) {
        return CalculatePosition(Self.Position, Self.GetHeadHeight(), Self.EntityId == Service.ClientState.LocalPlayer.EntityId, out fpp);
    }
    
    public unsafe Vector3 GetTargetPosition(out bool fpp) {
        return CalculatePosition(Self.TargetObject.Position, Self.TargetObject.GetHeadHeight(), Self.TargetObject.EntityId == Service.ClientState.LocalPlayer.EntityId, out fpp);
    }


    private void UpdateStateNewTarget() {
        bool fpp0;
        bool fpp1;
        Vector3 _source = GetSourcePosition(out fpp0);
        Vector3 _target = GetTargetPosition(out fpp1);
        Vector3 start = _source;
        Vector3 end = _target;

        float start_height = Self.GetCursorHeight();
        float end_height = Self.TargetObject.GetCursorHeight();
        float mid_height = (start_height + end_height) * 0.5f;

        float start_height_scaled = (fpp0 ? 0 : start_height) * Globals.Config.saved.HeightScale;
        float end_height_scaled = (fpp1 ? 0 : end_height) * Globals.Config.saved.HeightScale;

        float alpha = Math.Max(0, Math.Min(1, StateTime / Globals.Config.saved.NewTargetEaseTime));

        LastTargetHeight = end_height;
        MidHeight = mid_height;

        start.Y += start_height_scaled;
        end.Y += end_height_scaled;

        if (alpha >= 1) {
            State = LineState.Idle;
            LastTargetId = Self.TargetObject.EntityId;
        }

        Position = start;
        TargetPosition = Vector3.Lerp(start, end, alpha);
        LastTargetPosition2 = Vector3.Lerp(_source, _target, alpha);
    }

    private void UpdateStateDying_Anim(float mid_height) {
        float alpha = Math.Min(1, (StateTime / Globals.Config.saved.NoTargetFadeTime) * Globals.Config.saved.DeathAnimationTimeScale);

        switch (Globals.Config.saved.DeathAnimation) {
            case (LineDeathAnimation.Linear):
                MidHeight = MathUtils.Lerpf(mid_height, 0, alpha);
                break;
            case (LineDeathAnimation.Square):
                MidHeight = MathUtils.QuadraticLerpf(mid_height, 0, alpha);
                break;
            case (LineDeathAnimation.Cube):
                MidHeight = MathUtils.CubicLerpf(mid_height, 0, alpha);
                break;
        }
    }

    private void UpdateStateDying() {
        bool fpp;
        Vector3 _source = GetSourcePosition(out fpp);

        Vector3 start = _source;
        Vector3 end = LastTargetPosition;

        float start_height = Self.GetCursorHeight();
        float end_height = LastTargetHeight;
        float mid_height = (start_height + end_height) * 0.5f;

        float start_height_scaled = (fpp ? 0 : start_height) * Globals.Config.saved.HeightScale;
        float end_height_scaled = end_height * Globals.Config.saved.HeightScale;

        float alpha = Math.Max(0, Math.Min(1, StateTime / Globals.Config.saved.NoTargetFadeTime));

        UpdateStateDying_Anim(mid_height);

        start.Y += start_height_scaled;
        end.Y += end_height_scaled;

        if (alpha >= 1) {
            Sleeping = true;
        }

        Position = start;
        TargetPosition = Vector3.Lerp(end, start, alpha);
        LastTargetPosition2 = Vector3.Lerp(_source, LastTargetPosition, alpha);
    }

    private void UpdateStateSwitching() {
        bool fpp0;
        bool fpp1;
        Vector3 _source = GetSourcePosition(out fpp0);
        Vector3 _target = GetTargetPosition(out fpp1);

        Vector3 start = LastTargetPosition;
        Vector3 end = _target;

        float start_height = Self.GetCursorHeight();
        float end_height = Self.TargetObject.GetCursorHeight();
        float mid_height = (start_height + end_height) * 0.5f;

        float start_height_scaled = (fpp0 ? 0 : start_height) * Globals.Config.saved.HeightScale;
        float end_height_scaled = (fpp1 ? 0 : end_height) * Globals.Config.saved.HeightScale;

        float alpha = Math.Max(0, Math.Min(1, StateTime / Globals.Config.saved.NewTargetEaseTime));

        start.Y += LastTargetHeight * Globals.Config.saved.HeightScale;
        end.Y += end_height_scaled * Globals.Config.saved.HeightScale;

        if (alpha >= 1) {
            State = LineState.Idle;
            LastTargetId = Self.TargetObject.EntityId;
        }

        Position = _source;
        Position.Y += start_height_scaled * Globals.Config.saved.HeightScale;

        TargetPosition = Vector3.Lerp(start, end, alpha);
        LastTargetPosition2 = Vector3.Lerp(LastTargetPosition, _target, alpha);
        MidHeight = MathUtils.Lerpf(LastMidHeight, mid_height, alpha);
    }

    private void UpdateStateIdle() {
        bool fpp0;
        bool fpp1;
        Vector3 _source = GetSourcePosition(out fpp0);
        Vector3 _target = GetTargetPosition(out fpp1);

        float start_height = Self.GetCursorHeight();
        float end_height = Self.TargetObject.GetCursorHeight();
        float mid_height = (start_height + end_height) * 0.5f;

        float start_height_scaled = (fpp0 ? 0 : start_height) * Globals.Config.saved.HeightScale;
        float end_height_scaled = (fpp1 ? 0 : end_height) * Globals.Config.saved.HeightScale;

        LastTargetHeight = end_height;
        MidHeight = mid_height;

        Position = _source;

        TargetPosition = _target;
        LastTargetPosition = TargetPosition;
        LastTargetPosition2 = LastTargetPosition;

        Position.Y += start_height_scaled;
        TargetPosition.Y += end_height_scaled;
    }

    private unsafe void UpdateState() {
        bool new_target = false;

        if (State != LineState.Dying2) {
            if (HasTarget != HadTarget) {
                if (HasTarget) {
                    if (State == LineState.Dying) {
                        LastTargetPosition = LastTargetPosition2;
                    }

                    LastTargetId = Self.TargetObject.EntityId;
                    State = LineState.NewTarget;
                    StateTime = 0;
                }
                else {
                    if (State == LineState.Switching || State == LineState.NewTarget) {
                        LastTargetPosition = LastTargetPosition2;
                    }

                    State = LineState.Dying;
                    StateTime = 0;
                }
            }

            if (HasTarget && HadTarget) {
                if (Self.TargetObject.EntityId != LastTargetId) {
                    LastTargetId = Self.TargetObject.EntityId;
                    new_target = true;
                }

                if (new_target) {
                    if (State == LineState.Switching) {
                        LastTargetPosition = LastTargetPosition2;
                    }

                    State = LineState.Switching;
                    LastMidHeight = MidHeight;
                    StateTime = 0;
                }
            }
        }

        switch (State) {
            case LineState.NewTarget:
                UpdateStateNewTarget();
                break;
            case LineState.Dying:
            case LineState.Dying2:
                UpdateStateDying();
                break;
            case LineState.Switching:
                UpdateStateSwitching();
                break;
            case LineState.Idle:
                UpdateStateIdle();
                break;
        }

        UpdateMidPosition();

        StateTime += Globals.Framework->FrameDeltaTime;
        HadTarget = HasTarget;
    }

    private void UpdateColors() {
        float alpha = 1.0f;
        RGBA tempLineColor = Globals.Config.saved.LineColor.Color;
        RGBA tempOutlineColor = tempLineColor;

        if (Globals.Config.saved.LineColor.Visible) {
            LineColor.raw = Globals.Config.saved.LineColor.Color.raw;
            OutlineColor.raw = Globals.Config.saved.LineColor.OutlineColor.raw;
        }

        if (Self.TargetObject == null) {
            LineColor.raw = LastLineColor.raw;
            OutlineColor.raw = LastOutlineColor.raw;
        }
        else {
            int highestPriority = -1;
            foreach (TargetSettingsPair settings in Globals.Config.LineColors) {
                int priority = settings.GetPairPriority();
                if (priority > highestPriority && settings != null) {
                    TargetSettings SelfSettings = Self.GetTargetSettings();
                    TargetSettings TargSettings = Self.TargetObject.GetTargetSettings();

                    bool should_copy = CompareTargetSettings(ref settings.From, ref SelfSettings);
                    if (should_copy) {
                        should_copy = CompareTargetSettings(ref settings.To, ref TargSettings);
                    }
                    if (should_copy) {
                        highestPriority = priority;
                        LineSettings = settings;
                        tempLineColor.raw = settings.LineColor.Color.raw;
                        tempOutlineColor.raw = settings.LineColor.OutlineColor.raw;
                    }
                }
            }

            LineColor.raw = tempLineColor.raw;
            OutlineColor.raw = tempOutlineColor.raw;
            LastLineColor.raw = LineColor.raw;
            LastOutlineColor.raw = OutlineColor.raw;
        }

        if (Globals.Config.saved.BreathingEffect) {
            alpha = (1.0f - Globals.Config.saved.WaveAmplitudeOffset) + (float)Math.Cos(Globals.Runtime * Globals.Config.saved.WaveFrequencyScalar) * Globals.Config.saved.WaveAmplitudeOffset;
        }

        LineColor.a = (byte)((float)LineColor.a * alpha);
        OutlineColor.a = (byte)((float)LineColor.a * alpha);
    }

    private bool UpdateVisibility() {
        bool occlusion = Globals.Config.saved.OcclusionCulling;

#if (!PROBABLY_BAD)
        if (Self.GetIsBattleNPC()) {
            occlusion = true;
        }
#endif

        bool vis0 = Self.IsVisible(occlusion);
        bool vis1 = false;
        bool vis2 = false;

        if (HasTarget) {
            vis1 = Self.TargetObject.IsVisible(occlusion);
        }
        else {
            vis1 = Globals.IsVisible(TargetPosition, occlusion);
        }

        vis2 = Globals.IsVisible(MidPosition, occlusion);

        DrawBeginCap = Service.GameGui.WorldToScreen(Position, out ScreenPos);
        DrawEndCap = Service.GameGui.WorldToScreen(TargetPosition, out TargetScreenPos);
        DrawMid = Service.GameGui.WorldToScreen(MidPosition, out MidScreenPos);

        if (Globals.Config.saved.SolidColor == false) {
            for (int index = 0; index < SampleCount; index++) {
                float t = index * LinePointStep;
                Vector3 point = LineSettings.LineColor.UseQuad
                    ? MathUtils.EvaluateQuadratic(Position, MidPosition, TargetPosition, t)
                    : MathUtils.EvaluateCubic(Position, MidPosition, MidPosition, TargetPosition, t);

                bool vis = Service.GameGui.WorldToScreen(point, out Vector2 screenPoint);
                Points[index].Pos = screenPoint;
                Points[index].Visible = vis;
                Points[index].Dot = Globals.GetAngleThetaToCamera(point);
            }
        }

        if (!(DrawBeginCap || DrawEndCap || DrawMid)) {
            return false;
        }

        if (occlusion) {
            if (!DrawBeginCap) {
                vis0 = false;
            }

            if (!DrawEndCap) {
                vis1 = false;
            }

            if (vis0 || vis1 || vis2) {
                return true;
            }
            return false;
        }

        return true;
    }

    private void UpdateSampleCount() {
        int sampleCountTarget = 3;
        if (Globals.Config.saved.SolidColor == false) {
            if (Globals.Config.saved.DynamicSampleCount) {
                int min = Globals.Config.saved.TextureCurveSampleCountMin;
                int max = Globals.Config.saved.TextureCurveSampleCountMax;
                float thickScalar = Globals.Config.saved.LineThickness / 32.0f;
                if (thickScalar < 1.0f) {
                    thickScalar = 1.0f;
                }

                // further reduce quality as lines become more numerous
                var r = Math.Max(TargetLineManager.RenderedLineCount, 1);
                max = Math.Max(min + 3, max);
                max = Math.Min(max, (7 * max) / r);

                sampleCountTarget = min + ((int)Math.Floor(1.5f + (TargetPosition - Position).Length())) * 2;
                if (sampleCountTarget > max) {
                    sampleCountTarget = max;
                }

                sampleCountTarget = (int)MathF.Floor(sampleCountTarget * thickScalar);

                // less chonky lines in first person
                if (Globals.IsInFirstPerson()) {
                    sampleCountTarget *= 2;
                }
            }
            else {
                sampleCountTarget = Globals.Config.saved.TextureCurveSampleCount;
            }

            sampleCountTarget = Math.Min(Math.Max(Globals.Config.saved.TextureCurveSampleCountMin, sampleCountTarget), Globals.Config.saved.TextureCurveSampleCountMax);
            sampleCountTarget -= (~sampleCountTarget & 1); // make it odd so there is a peak
            if (SampleCount != sampleCountTarget) {
                InitializeLinePoints(sampleCountTarget);
            }
        }
    }

    public unsafe void Update() {
        if (Self == null || Self.IsValid() == false) {
            Sleeping = true;
            return;
        }

        var csObj = Self.GetClientStructGameObject();
        if (csObj == null) {
            return;
        }

        if (((csObj->RenderFlags & 0x800) != 0 || Self.IsDead) && State != LineState.Dying2) {
            State = LineState.Dying2;
            StateTime = 0;
        }

        HasTarget = Self.TargetObject != null;

        UpdateState();
        UpdateColors();
        UpdateSampleCount();

        if (Service.ClientState.IsPvP && Globals.HandlePvP) {
            TrollCheaters();
        }
    }

    public unsafe bool Draw() {
        if (Sleeping) {
            return false;
        }

        if (!UpdateVisibility()) {
            return false;
        }
        if (Globals.Config.saved.SolidColor) {
            DrawSolidLine();
        }
        else {
            DrawFancyLine();
        }

        if (Globals.Config.saved.DebugDynamicSampleCount) {
            ImDrawListPtr drawlist = ImGui.GetWindowDrawList();
            var sampleCountString = SampleCount.ToString();
            var pos2 = ScreenPos;
            var pos3 = ScreenPos;
            pos2.Y += 32;
            pos3.Y += 64;
            drawlist.AddText(ScreenPos, 0xFF000000, sampleCountString);
            drawlist.AddText(MidScreenPos, 0xFF00FFFF, sampleCountString);
            drawlist.AddText(TargetScreenPos, 0xFFFFFFFF, sampleCountString);
            drawlist.AddText(pos2, 0xFFFFFFFF, $"EntityId: {Self.EntityId:X}, GameObjectId: {Self.GameObjectId:X}; State: {State}");
            drawlist.AddText(pos3, 0xFFFFFFFF, $"Dead? {Self.IsDead}; Render Flags: {Self.GetClientStructGameObject()->RenderFlags:X}");
        }

        return true;
    }

    private unsafe void TrollCheaters() {
        var group = GroupManager.Instance();
        var chara = CharacterManager.Instance();
        uint partyMemberNewTarget = 0xE0000000;

        // friendlies target any existing targeted drk, otherwise any tank
        for (int index = 0; index < 24; index++) {
            var partymember = group->MainGroup.GetAllianceMemberByIndex(index);
            if (partymember != null) {
                var me = chara->LookupBattleCharaByEntityId(partymember->EntityId);
                if (me != null) {
                    var target = chara->LookupBattleCharaByEntityId((uint)me->Character.TargetId);
                    if (target != null) {
                        if (target->Character.GameObject.ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Pc) {
                            var _target = (Character*)target;
                            if (_target->CharacterData.ClassJob == (byte)ClassJob.DarkKnight) {
                                partyMemberNewTarget = partymember->EntityId;
                                break;
                            }

                            if (_target->CharacterData.ClassJob == (byte)ClassJob.Gunbreaker || _target->CharacterData.ClassJob == (byte)ClassJob.Paladin || _target->CharacterData.ClassJob == (byte)ClassJob.Warrior) {
                                partyMemberNewTarget = partymember->EntityId;
                            }
                        }
                    }
                }
            }
        }

        if (Globals.HandlePvPTime > 150 && Self.GetIsPlayerCharacter()) {
            var player = (Character*)Self.Address;
            if (player->GameObject.EntityId == Service.ClientState.LocalPlayer?.EntityId) {
                return;
            }
            
            if (player->IsAllianceMember || player->IsPartyMember) {
                if (partyMemberNewTarget != 0xE0000000) {
                    player->TargetId = partyMemberNewTarget;
                }
            }
            else if (player->GameObject.ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Pc) {
                // probably a baddie, target the player character
                if (Service.ClientState.LocalPlayer != null) {
                    player->TargetId = Service.ClientState.LocalPlayer.EntityId;
                }
            }
        }
    }
}
