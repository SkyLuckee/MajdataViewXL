using MajdataViewX.Base;
using MajdataViewX.Notes;
using MajdataViewX.Notes.SlideUtils;
using MajdataViewX.Types.Enums;
using MajdataViewX.Types.Input;
using MajdataViewX.Utils.Extensions;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static MajdataViewX.Base.MajBurst;

namespace MajdataViewX.Managers
{
    public partial class NoteManager
    {
        // 同一 timing 内收集到的 touch 类 hit；LoadTiming 末尾做双圆预合并后写入 hits。
        private NativeList<DJAutoHitData> _djAutoTouchHitsThisTiming = new(64, Allocator.Persistent);

        public const float DJAUTO_TAP_RELEASE_TIME_SEC = 0.022f;
        public const float DJAUTO_HOLD_RELEASE_TIME_SEC = 0.022f;
        public const float DJAUTO_TOUCH_RELEASE_TIME_SEC = 0.022f;
        public const float DJAUTO_TOUCHHOLD_RELEASE_TIME_SEC = 0.022f;

        // Tap/Hold/Slide默认尺寸
        public const float DJAUTO_HAND_RADIUS = 0.45f;
        // Wifi默认尺寸
        public const float DJAUTO_WIFI_RADIUS = 1.00f;
        // 所有 DJAuto 手势复用时允许扩大的最大半径。
        public const float DJAUTO_HAND_MAX_RADIUS = 1.80f;

        /// <summary>长 touchhold 重算手位的间隔阈值：相邻 endtime gap >= 此值则断开新段、重新算圆。</summary>
        private const float TOUCH_HIT_RESIZE_HAND_THRESHOLD = 100f * MajCtx.FRAME_LENGTH_SEC;
        private const float TOUCH_HIT_SHORT_SPLIT_THRESHOLD = 5f * MajCtx.FRAME_LENGTH_SEC + (float)MajGeo.Epsilon;

        /// <summary>双圆枚举的 3 点最小覆盖圆候选仅在 n ≤ 此值时启用。
        /// 3 点候选使双圆预合并从 O(n^5) 升到 O(n^7)，n=33 全 sensor 时会卡 ~3s，故大 n 降级为 1/2 点候选。</summary>
        private const int TWO_CIRCLE_3POINT_CANDIDATE_MAX_N = 16;

        // DJAuto打星星的放手时机（判定后）
        public const float DJAUTO_SLIDE_RELEASE_DELAY_SEC = 6 * MajCtx.FRAME_LENGTH_SEC;


        /// <summary>
        /// 本 timing 的 touch hit 双圆预合并：双手只有两只，用最多两个覆盖圆覆盖本 timing 尽量多的 touch 落点。
        /// 第一圆候选 = 每 1/2/3 点的最小覆盖圆；剩余点交给第二圆同样「尽量多管」（≤MAX 覆盖最多点的单圆）。
        /// 半径上限 DJAUTO_HAND_MAX_RADIUS、下限 DJAUTO_HAND_RADIUS；选优：合计覆盖点数最多 -> max(r1,r2) 最小 -> |r1-r2| 最小。
        /// 超上限废弃；覆盖不到的点不生成 hit -> Miss 看命。计算由 Burst 直接把结果 Add 进 hits（单线程，无竞争）。
        /// </summary>
        private void CombineTouchHitsThisTiming()
        {
            var src = _djAutoTouchHitsThisTiming;
            int n = src.Length;
            if (n == 0) return;
            if (n == 1)
            {
                hits.Add(src[0]);
                src.Clear();
                return;
            }

            CombineTouchHitsBurst(src.AsArray(), DJAUTO_HAND_RADIUS, DJAUTO_HAND_MAX_RADIUS, ref hits);
            src.Clear();
        }

        [BurstCompile]
        private static void CombineTouchHitsBurst(
            NativeArray<DJAutoHitData> src,
            float handRadius,
            float maxRadius,
            ref NativeList<DJAutoHitData> hits)
        {
            int n = src.Length;
            // 同一 timing 内所有 touch 的 start time 相同，直接取。
            float startMin = src[0].StartTime;
            float maxEnd = float.MinValue;
            for (int i = 0; i < n; i++)
                if (src[i].EndTime > maxEnd) maxEnd = src[i].EndTime;

            // 第一次：全部 touch 一起算双圆。
            var first = ComputeTwoCircle(src, handRadius, maxRadius);

            float split = startMin + TOUCH_HIT_SHORT_SPLIT_THRESHOLD;

            // 最大 endtime 不超阈值 -> 只输出这一次，endtime 取 maxEnd。
            if (maxEnd <= split)
            {
                EmitCircles(ref hits, first, startMin, maxEnd);
                return;
            }

            // 超阈值：第一次算好的 1或2 个 hit 作为短按，endtime 用 release 覆盖。
            EmitCircles(ref hits, first, startMin, startMin + DJAUTO_TOUCH_RELEASE_TIME_SEC);

            // 长 touchhold（endtime > 阈值）按 endtime 升序排序。遍历中 gap >= TOUCH_HIT_RESIZE_HAND_THRESHOLD 断开：
            // 对「当前段及之后全部未结束的 hit」重新算一次双圆（= 前面遍历过的 + 后面未遍历的），
            // start 取上一段末（第一段为 split），end 取本段末（最后一个遍历到的最大 endtime）。如此往复到最后一个。
            var longs = new NativeList<DJAutoHitData>(n, Allocator.Temp);
            for (int i = 0; i < n; i++)
                if (src[i].EndTime > split) longs.Add(src[i]);
            if (longs.Length > 0)
            {
                SortByEndTime(longs);
                int m = longs.Length;
                int segIndex = 0;
                float segBegin = split;
                for (int i = 1; i <= m; i++)
                {
                    bool breakHere = i == m
                        || (longs[i].EndTime - longs[i - 1].EndTime) >= TOUCH_HIT_RESIZE_HAND_THRESHOLD;
                    if (!breakHere) continue;

                    int remLen = m - segIndex;
                    var rem = new NativeArray<DJAutoHitData>(remLen, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    for (int j = 0; j < remLen; j++) rem[j] = longs[segIndex + j];
                    var segBest = ComputeTwoCircle(rem, handRadius, maxRadius);
                    EmitCircles(ref hits, segBest, segBegin, longs[i - 1].EndTime);
                    rem.Dispose();
                    segBegin = longs[i - 1].EndTime;
                    segIndex = i;
                }
            }
            longs.Dispose();
        }

        /// <summary>对给定子集做双圆枚举（1/2/3 点候选 + 第二圆尽量多管），返回最优两圆，不写 hits。</summary>
        [BurstCompile]
        private static TwoCircleBest ComputeTwoCircle(
            NativeArray<DJAutoHitData> src, float handRadius, float maxRadius)
        {
            int n = src.Length;
            var best = new TwoCircleBest { Max = float.MaxValue, Diff = float.MaxValue };
            if (n == 0) return best;

            for (int i = 0; i < n; i++)
                ConsiderFirst(src, handRadius, maxRadius, new Circle { C = src[i].Pos, R = 0f }, ref best);
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    ConsiderFirst(src, handRadius, maxRadius, MinEnclosing2(src[i].Pos, src[j].Pos), ref best);
            if (n <= TWO_CIRCLE_3POINT_CANDIDATE_MAX_N)
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++)
                        for (int k = j + 1; k < n; k++)
                            ConsiderFirst(src, handRadius, maxRadius, MinEnclosing3(src[i].Pos, src[j].Pos, src[k].Pos), ref best);

            return best;
        }

        /// <summary>把最优两圆以统一的 startTime/endTime 写入 hits（0/1/2 个）。</summary>
        [BurstCompile]
        private static void EmitCircles(ref NativeList<DJAutoHitData> hits, TwoCircleBest best, float startTime, float endTime)
        {
            if (!best.HasC1) return;
            hits.Add(new DJAutoHitData(best.C1, best.R1, startTime, endTime, false));
            if (best.HasC2)
                hits.Add(new DJAutoHitData(best.C2, best.R2, startTime, endTime, false));
        }

        /// <summary>按 EndTime 升序插入排序（长 touchhold 通常很少，Burst 友好）。</summary>
        [BurstCompile]
        private static void SortByEndTime(NativeList<DJAutoHitData> list)
        {
            for (int i = 1; i < list.Length; i++)
            {
                var key = list[i];
                int j = i - 1;
                while (j >= 0 && list[j].EndTime > key.EndTime)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = key;
            }
        }

        [BurstCompile]
        private static void ConsiderFirst(
            NativeArray<DJAutoHitData> src, float handRadius, float maxRadius,
            Circle first, ref TwoCircleBest best)
        {
            float r1 = math.max(first.R, handRadius);
            if (r1 > maxRadius) return;

            float2 cc = first.C;
            int n = src.Length;
            var remaining = new NativeList<float2>(n, Allocator.Temp);
            for (int i = 0; i < n; i++)
                if (math.distance(src[i].Pos, cc) > r1 + MajGeo.Epsilon)
                    remaining.Add(src[i].Pos);

            int covered1 = n - remaining.Length;
            int covered;
            float maxR, diff, r2 = 0f;
            float2 c2 = default;
            bool hasC2;
            if (remaining.Length == 0)
            {
                covered = n;
                maxR = r1; diff = r1; hasC2 = false;
            }
            else
            {
                var sb = BestSingleCircle(remaining, handRadius, maxRadius);
                r2 = sb.R;
                c2 = sb.C;
                covered = covered1 + sb.Cov;
                maxR = math.max(r1, r2);
                diff = math.abs(r1 - r2);
                hasC2 = true;
            }
            remaining.Dispose();

            if (covered > best.Covered
                || (covered == best.Covered && (maxR < best.Max
                    || (math.abs(maxR - best.Max) < 1e-6f && diff < best.Diff))))
            {
                best.Covered = covered;
                best.Max = maxR;
                best.Diff = diff;
                best.HasC1 = true;
                best.C1 = cc; best.R1 = r1;
                best.C2 = c2; best.R2 = r2;
                best.HasC2 = hasC2;
            }
        }

        /// <summary>在 pts 中选 1 个 ≤MAX 的圆（1/2/3 点候选）覆盖 pts 中最多点。</summary>
        [BurstCompile]
        private static SingleBest BestSingleCircle(NativeList<float2> pts, float handRadius, float maxRadius)
        {
            float2 bestC = default;
            float bestR = float.MaxValue;
            int bestCov = 0;
            int n = pts.Length;

            for (int i = 0; i < n; i++)
                ConsiderSingle(pts, handRadius, maxRadius, new Circle { C = pts[i], R = 0f }, ref bestC, ref bestR, ref bestCov);
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    ConsiderSingle(pts, handRadius, maxRadius, MinEnclosing2(pts[i], pts[j]), ref bestC, ref bestR, ref bestCov);
            if (n <= TWO_CIRCLE_3POINT_CANDIDATE_MAX_N)
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++)
                        for (int k = j + 1; k < n; k++)
                            ConsiderSingle(pts, handRadius, maxRadius, MinEnclosing3(pts[i], pts[j], pts[k]), ref bestC, ref bestR, ref bestCov);

            return new SingleBest { C = bestC, R = bestR, Cov = bestCov };
        }

        [BurstCompile]
        private static void ConsiderSingle(
            NativeList<float2> pts, float handRadius, float maxRadius, Circle cand,
            ref float2 bestC, ref float bestR, ref int bestCov)
        {
            float r = math.max(cand.R, handRadius);
            if (r > maxRadius) return;
            int cov = 0;
            int n = pts.Length;
            for (int i = 0; i < n; i++)
                if (math.distance(pts[i], cand.C) <= r + MajGeo.Epsilon) cov++;
            if (cov > bestCov || (cov == bestCov && r < bestR))
            {
                bestCov = cov;
                bestC = cand.C;
                bestR = r;
            }
        }

        [BurstCompile]
        private static Circle MinEnclosing2(float2 a, float2 b)
        {
            var c = (a + b) * 0.5f;
            return new Circle { C = c, R = math.distance(a, b) * 0.5f };
        }

        /// <summary>3 点最小覆盖圆：钝角/共线退化为最长边直径圆，否则外接圆。</summary>
        [BurstCompile]
        private static Circle MinEnclosing3(float2 a, float2 b, float2 c)
        {
            float d2_01 = math.distancesq(a, b);
            float d2_12 = math.distancesq(b, c);
            float d2_20 = math.distancesq(c, a);

            int longest = 0; float maxD2 = d2_01;
            if (d2_12 > maxD2) { longest = 1; maxD2 = d2_12; }
            if (d2_20 > maxD2) { longest = 2; maxD2 = d2_20; }

            float sumOther = longest switch
            {
                0 => d2_12 + d2_20,
                1 => d2_01 + d2_20,
                _ => d2_01 + d2_12,
            };
            if (maxD2 >= sumOther || maxD2 < 1e-12f)
            {
                return longest switch
                {
                    0 => MinEnclosing2(a, b),
                    1 => MinEnclosing2(b, c),
                    _ => MinEnclosing2(c, a),
                };
            }

            float ax = a.x, ay = a.y, bx = b.x, by = b.y, cx = c.x, cy = c.y;
            float d = 2f * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (math.abs(d) < 1e-12f)
                return longest == 0 ? MinEnclosing2(a, b)
                    : longest == 1 ? MinEnclosing2(b, c) : MinEnclosing2(c, a);

            float a2 = ax * ax + ay * ay;
            float b2 = bx * bx + by * by;
            float c2 = cx * cx + cy * cy;
            float ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
            float uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
            var center = new float2(ux, uy);
            return new Circle { C = center, R = math.distance(center, a) };
        }

        [BurstCompile]
        private struct TwoCircleBest
        {
            public int Covered;
            public float Max, Diff;
            public bool HasC1, HasC2;
            public float2 C1, C2;
            public float R1, R2;
        }

        [BurstCompile]
        private struct SingleBest
        {
            public float2 C;
            public float R;
            public int Cov;
        }

        [BurstCompile]
        private struct Circle { public float2 C; public float R; }

        /// <summary>
        /// DJAuto 双手自动演奏：把本帧活跃的 hits/swipes 通过 InputData 的修改函数转成 ActiveDown + 手圆渲染。
        /// 外键 hit(ButtonPos&gt;=0) 走 HandleButtonInput（写 buttonStates，OnLateUpdate 渲染按键）；
        /// 世界坐标 hit/swipe 走 HandleWorldPosInput（命中 sensor + 渲染手圆），swipe 位置由 Arrows 弧长插值得出。
        /// </summary>
        [BurstCompile]
        private unsafe struct HitSwipeUpdateJob : IJob
        {
            public NativeArray<DJAutoHitData> hits;
            public NativeArray<DJAutoSwipeData> swipes;

            public void Execute()
            {
                var time = TimeData.NoteTime;

                for (int i = 0; i < hits.Length; i++)
                {
                    ref readonly var hit = ref hits.ElementRef(i);
                    if (time < hit.StartTime || time > hit.EndTime) continue;

                    if (hit.ButtonPos >= 0)
                        InputData.HandleButtonInput((SensorType)hit.ButtonPos, true);
                    else
                        InputData.HandleWorldPosInput(hit.Pos, hit.Radius);
                }

                for (int i = 0; i < swipes.Length; i++)
                {
                    ref readonly var swipe = ref swipes.ElementRef(i);
                    if (time < swipe.StartTime || time > swipe.ReleaseTime || time > swipe.EndTime) continue;
                    if (swipe.IsWifi)
                        HandleWifiSwipe(swipe, time);
                    else
                        HandleSlideSwipe(swipe, time);
                }
            }

            /// <summary>
            /// 沿 swipe.Arrows 做弧长参数化插值，镜像 SlideUpdateJob 的非 wifi 星星位置算法。
            /// wifi 时此为基础位置（C 支），HandleWifiSwipe 再绕起点 ±22.5° 派生双手 L/R。
            /// </summary>
            private static float2 ComputeSwipePos(DJAutoSwipeData swipe, float time)
            {
                var arrows = swipe.Arrows;
                int count = swipe.ArrowCount;
                if (count <= 1 || arrows == null) return float2.zero;

                var duration = swipe.EndTime - swipe.StartTime;
                var progress = duration > 0f ? math.saturate((time - swipe.StartTime) / duration) : 0f;

                int idxLast = count - 1;
                var distance = progress * arrows[idxLast].L;
                int processIdx = 1;
                while (processIdx < idxLast && arrows[processIdx].L < distance) processIdx++;
                var p0 = arrows[processIdx - 1];
                var p1 = arrows[processIdx];
                var t = math.unlerp(p0.L, p1.L, distance);
                return new float2(math.lerp(p0.X, p1.X, t), math.lerp(p0.Y, p1.Y, t));
            }

            private static void HandleSlideSwipe(DJAutoSwipeData swipe, float time)
            {
                var pos = ComputeSwipePos(swipe, time);
                InputData.HandleWorldPosInput(pos, swipe.Radius);
            }

            /// <summary>
            /// wifi 双手：双手绕起点 ±11.25° 旋转（L/R 支与 C 支的中间）。
            /// </summary>
            private static void HandleWifiSwipe(DJAutoSwipeData swipe, float time)
            {
                // 基础位置（C 支）
                var posC = ComputeSwipePos(swipe, time);
                // 双手：基础位置相对起点的偏移绕起点旋转 ±11.25°
                var arrows = swipe.Arrows;
                var startPos = new float2(arrows[0].X, arrows[0].Y);
                var offset = posC - startPos;
                var rad = math.radians(11.25f);
                var cos = math.cos(rad);
                var sin = math.sin(rad);
                // 顺时针 11.25°（L 支方向）
                var posL = startPos + new float2(offset.x * cos + offset.y * sin, -offset.x * sin + offset.y * cos);
                // 逆时针 11.25°（R 支方向）
                var posR = startPos + new float2(offset.x * cos - offset.y * sin, offset.x * sin + offset.y * cos);
                InputData.HandleWorldPosInput(posL, swipe.Radius);
                InputData.HandleWorldPosInput(posR, swipe.Radius);
            }
        }
    }
}
