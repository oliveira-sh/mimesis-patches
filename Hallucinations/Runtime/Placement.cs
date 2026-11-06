// MIMESIS Hallucinations - living player: where a hallucination appears (outside the player's view)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The affected player's game picks the spot because only it knows the real view: Camera.main's position, rotation,
// field of view (the Fov patch included) and aspect, and what walls hide. The A* navmesh is built on every client
// (GamePlayScene -> BuildNavMesh -> Hub.s.navman.Build()), so it can also check the spot is walkable and reachable.
// Deterministic from the host's seed (System.Random), so a log line can be reproduced.
// Out of the (slightly widened) view first, in every ring; only when none of those works, a spot inside the view that
// walls hide completely (every ray from the eye to the fake's outline, weapon included, is blocked).
// If this game has no usable navmesh, a physics-only fallback looks for a floor spot behind the
// camera with a straight, door-free walk to the player; the fake then walks straight at them.

using System;
using System.Collections.Generic;
using Mimic.Actors;
using UnityEngine;

namespace Hallucinations
{
    static class Placement
    {
        static readonly float[] Rings = { 9f, 6f, 12f, 4f };      // 4 m only as a last resort
        const int Directions = 16;                                  // every 22.5 degrees
        const float MaxFloorStep = 1.8f;                            // same floor as the player, roughly
        const int RoutesPerRing = 3;
        static readonly float[] OutlineHeights = { 0.3f, 1.2f, 1.8f };  // feet, chest, head
        const float OutlineHalfWidth = 0.4f;                            // shoulders
        const float WeaponReach = 0.6f;                                 // a weapon held out toward the player

        struct Candidate
        {
            public Vector3 Pos;
            public float Score;     // lower = better
            public bool OutOfView;  // else hidden behind walls
        }

        static readonly List<Candidate> _candidates = new List<Candidate>(Directions);
        static readonly Vector4[] _planes = new Vector4[6];

        /// <summary>A walkable spot 6-12 m from the avatar (4 m last resort), on its floor and indoor/outdoor side, outside
        /// the camera's view or hidden behind walls, reachable without a closed door. False = nowhere (NoSpot).
        /// navOk: the spot came from the navmesh (else the fallback: the fake must walk straight).</summary>
        internal static bool TryFind(GameMainBase main, ProtoActor avatar, int seed, out Vector3 pos, out float yaw, out string where, out bool navOk)
        {
            pos = Vector3.zero;
            yaw = 0f;
            where = "";
            navOk = false;
            var cam = Camera.main;
            if (main == null || avatar == null || cam == null) return false;
            var rnd = new System.Random(seed);
            Vector3 p = avatar.transform.position;
            var scene = main as GamePlayScene;
            bool? indoor0 = IndoorAt(scene, p);

            // A slightly wider view than the camera's, so the fake isn't seen popping in at the edge of the screen.
            float fov = Mathf.Clamp(cam.fieldOfView + 15f, 30f, 170f);
            var proj = Matrix4x4.Perspective(fov, Mathf.Max(0.1f, cam.aspect), Mathf.Max(0.01f, cam.nearClipPlane), 60f);
            FrustumPlanes(proj * cam.worldToCameraMatrix, _planes);
            Vector3 eye = cam.transform.position;
            Vector3 camFwd = Flat(cam.transform.forward);

            var nav = Hub.s != null ? Hub.s.navman : null;
            navOk = nav != null && NavMeshConstants.IsValid(SafeNearest(nav, p, 2f));
            float offset = (float)(rnd.NextDouble() * 360.0);

            // Pass 0: out of view only. Pass 1 (none of those works): inside the view but hidden behind walls.
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantOutOfView = pass == 0;
                foreach (float ring in Rings)
                {
                    _candidates.Clear();
                    for (int k = 0; k < Directions; k++)
                    {
                        float a = (offset + k * (360f / Directions)) * Mathf.Deg2Rad;
                        var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                        Vector3 q = p + dir * ring;
                        Vector3 n;
                        if (navOk)
                        {
                            n = SafeNearest(nav, q, 2f);
                            if (!NavMeshConstants.IsValid(n)) continue;
                        }
                        else if (!FloorBelow(q, p.y, out n)) continue;
                        if (Mathf.Abs(n.y - p.y) >= MaxFloorStep) continue;
                        if ((Flat(n - p)).magnitude < 3f) continue;
                        if (indoor0.HasValue)
                        {
                            var i = IndoorAt(scene, n);
                            if (i.HasValue && i.Value != indoor0.Value) continue;
                        }

                        bool outOfView = !InFrustum(_planes, n + Vector3.up * 0.95f, new Vector3(0.4f, 0.95f, 0.4f));
                        if (outOfView != wantOutOfView) continue;
                        if (!outOfView && !Occluded(eye, n)) continue;
                        if (navOk && !Safe(() => nav.IsSafelyInsideNavMesh(n, 0.4f))) continue;
                        if (navOk && !Safe(() => nav.IsReachable(p, n))) continue;
                        if (!navOk && !StraightWalk(n, p)) continue;

                        // Behind the camera first, then a distance near the ring's, plus a little seeded variety.
                        float behind = Vector3.Dot(camFwd, Flat(n - p).normalized);   // -1 = right behind
                        float score = behind + Mathf.Abs(Flat(n - p).magnitude - ring) * 0.05f + (float)rnd.NextDouble() * 0.15f;
                        _candidates.Add(new Candidate { Pos = n, Score = score, OutOfView = outOfView });
                    }
                    if (_candidates.Count == 0) continue;
                    _candidates.Sort((x, y) => x.Score.CompareTo(y.Score));
                    Log.Trace($"Placement ring {ring} m: {_candidates.Count} {(wantOutOfView ? "out-of-view" : "behind-a-wall")} candidates");

                    int tries = Math.Min(RoutesPerRing, _candidates.Count);
                    for (int c = 0; c < tries; c++)
                    {
                        var cand = _candidates[c];
                        if (navOk && !RouteOk(nav, cand.Pos, p, ring)) continue;
                        pos = cand.Pos;
                        var look = Flat(p - pos);
                        yaw = look.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(look).eulerAngles.y : 0f;
                        where = (cand.OutOfView ? "out of view" : "behind a wall") + (navOk ? "" : ", no navmesh: straight walk");
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Inside the view, but walls hide all of it: every ray from the eye to its outline (both shoulders and
        /// the middle at feet, chest and head height, and a weapon held out toward the player) is blocked.</summary>
        static bool Occluded(Vector3 eye, Vector3 feet)
        {
            Vector3 toEye = Flat(eye - feet);
            Vector3 fwd = toEye.sqrMagnitude > 0.0001f ? toEye.normalized : Vector3.forward;
            Vector3 side = new Vector3(fwd.z, 0f, -fwd.x) * OutlineHalfWidth;
            foreach (float h in OutlineHeights)
            {
                Vector3 c = feet + Vector3.up * h;
                // CheckBlockByWall is true when the line is clear.
                if (PhysicsUtility.CheckBlockByWall(eye, c) || PhysicsUtility.CheckBlockByWall(eye, c + side) ||
                    PhysicsUtility.CheckBlockByWall(eye, c - side)) return false;
            }
            return !PhysicsUtility.CheckBlockByWall(eye, feet + Vector3.up * OutlineHeights[1] + fwd * WeaponReach);
        }

        /// <summary>A navmesh route from the spot to the player, not much longer than the straight distance, with no closed
        /// door on it (the client's navmesh doesn't know the host's door cuts).</summary>
        static bool RouteOk(PathFindManager nav, Vector3 from, Vector3 to, float ring)
        {
            PathFindResult route;
            try { route = nav.GetRoute(from, to, shouldArriveEndPoint: false); }
            catch (Exception) { return false; }
            if (route == null || !route.Success || route.PathPoints == null || route.PathPoints.Count == 0) return false;
            if (route.Length > 2.2f * ring) return false;
            var pts = route.PathPoints;
            var end = pts[pts.Count - 1];
            if (Flat(end - to).magnitude > 1.5f || Mathf.Abs(end.y - to.y) > 2f) return false; // a partial route: it can't get there
            if (PhysicsUtility.IsBlockedByClosedDoor(from, pts[0])) return false;
            for (int i = 0; i + 1 < pts.Count; i++)
                if (PhysicsUtility.IsBlockedByClosedDoor(pts[i], pts[i + 1])) return false;
            // The path may stop short of the player (shouldArriveEndPoint false): the last stretch must be open too.
            return !PhysicsUtility.IsBlockedByClosedDoor(pts[pts.Count - 1], to);
        }

        /// <summary>No navmesh: a clear, door-free straight line at knee height, with floor all the way.</summary>
        static bool StraightWalk(Vector3 from, Vector3 to)
        {
            if (PhysicsUtility.IsBlockedByClosedDoor(from, to)) return false;
            if (!PhysicsUtility.CheckBlockByWall(from + Vector3.up * 0.5f, to + Vector3.up * 0.5f)) return false;
            float len = Flat(to - from).magnitude;
            int steps = Mathf.Max(1, Mathf.CeilToInt(len));
            for (int i = 1; i < steps; i++)
            {
                var q = Vector3.Lerp(from, to, i / (float)steps);
                if (!FloorBelow(q, q.y, out var f) || Mathf.Abs(f.y - q.y) > 0.8f) return false;
            }
            return true;
        }

        /// <summary>The floor under q (searched from above the reference height), on the game's wall layer.</summary>
        internal static bool FloorBelow(Vector3 q, float refY, out Vector3 floor)
        {
            floor = q;
            var from = new Vector3(q.x, refY + 1.5f, q.z);
            if (!Physics.Raycast(from, Vector3.down, out var hit, 4f, Hub.DefaultOnlyLayerMask, QueryTriggerInteraction.Ignore)) return false;
            floor = hit.point;
            return true;
        }

        static bool? IndoorAt(GamePlayScene scene, Vector3 at)
        {
            if (scene == null) return null;
            try { return scene.TryCheckPosIsIndoor(in at, out bool indoor) ? indoor : (bool?)null; }
            catch (Exception) { return null; }
        }

        static Vector3 SafeNearest(PathFindManager nav, Vector3 at, float maxDistance)
        {
            try { return nav.GetNearestPointOnNavMesh(at, maxDistance); }
            catch (Exception) { return NavMeshConstants.INVALID_POSITION; }
        }

        static bool Safe(Func<bool> f)
        {
            try { return f(); }
            catch (Exception) { return false; }
        }

        internal static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // GeometryUtility's frustum helpers have Span overloads in Unity 6 that this project's reference set can't resolve,
        // so the same math by hand (Gribb/Hartmann planes of a world-to-clip matrix, normals inward; OpenGL clip space,
        // as Camera.projectionMatrix and Matrix4x4.Perspective use).

        /// <summary>The 6 planes (xyz = normal, w = distance) of worldToClip = projection * worldToCamera.</summary>
        internal static void FrustumPlanes(Matrix4x4 worldToClip, Vector4[] planes)
        {
            Vector4 r0 = worldToClip.GetRow(0), r1 = worldToClip.GetRow(1), r2 = worldToClip.GetRow(2), r3 = worldToClip.GetRow(3);
            planes[0] = r3 + r0; // left
            planes[1] = r3 - r0; // right
            planes[2] = r3 + r1; // bottom
            planes[3] = r3 - r1; // top
            planes[4] = r3 + r2; // near
            planes[5] = r3 - r2; // far
        }

        /// <summary>The box (center, half extents) is at least partly inside the planes.</summary>
        internal static bool InFrustum(Vector4[] planes, Vector3 center, Vector3 extents)
        {
            for (int i = 0; i < planes.Length; i++)
            {
                var p = planes[i];
                float r = extents.x * Mathf.Abs(p.x) + extents.y * Mathf.Abs(p.y) + extents.z * Mathf.Abs(p.z);
                if (p.x * center.x + p.y * center.y + p.z * center.z + p.w + r < 0f) return false;
            }
            return true;
        }
    }
}
