using System.Collections.Generic;
using MajdataViewX.Base;
using MajdataViewX.Notes;
using MajdataViewX.Notes.NoteDatas;
using MajdataViewX.Notes.SlideUtils;
using MajdataViewX.Types.Enums;
using MajdataViewX.Types.Input;
using MajdataViewX.Utils.Extensions;
using MajSimai;
using System.Threading;
using MajdataViewX.Types.Rendering;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static MajdataViewX.Base.MajBurst;
using Unity.Collections.LowLevel.Unsafe;

namespace MajdataViewX.Managers
{
    public partial class NoteManager
    {
        // 同一 timing 内收集到的 touch 类引用（指向 touches/touchHolds 的 index），LoadTiming 末尾做双圆预合并后写入 hits。
        private NativeList<DJAutoNoteRef> _djAutoTouchHitsThisTiming = new(64, Allocator.Persistent);
        // touch 组合(sensor 子集掩码) -> 双圆手法的缓存，重复 touch 模式只算一次
        private readonly Dictionary<ulong, TwoCircleBest> _touchComboCache = new();

        private const int DJAUTO_CURVE_RESOLUTION = 2048;
        private static NativeArray<float> _djAutoMoveCurve;

        public const float DJAUTO_TAP_RELEASE_TIME_SEC = 0.022f;
        public const float DJAUTO_HOLD_RELEASE_TIME_SEC = 0.022f;
        public const float DJAUTO_TOUCH_RELEASE_TIME_SEC = 0.022f;
        public const float DJAUTO_TOUCHHOLD_RELEASE_TIME_SEC = 0.022f;

        /// <summary>Tap/Hold外键默认半径</summary>
        public const float DJAUTO_BTN_DEFAULT_RADIUS = MajPos.MAIN_RADIUS + DJAUTO_HAND_RADIUS * 2 + 0.5f;

        /// <summary>Tap/Hold/Slide默认尺寸</summary>
        public const float DJAUTO_HAND_RADIUS = 0.45f;
        /// <summary>Wifi默认尺寸</summary>
        public const float DJAUTO_WIFI_RADIUS = 1.00f;
        /// <summary>所有 DJAuto 手最大半径。</summary>
        public const float DJAUTO_HAND_MAX_RADIUS = 1.80f;

        /// <summary>长 touchhold 重算手位的间隔阈值：相邻 endtime gap >= 此值则断开新段、重新算圆。</summary>
        private const float TOUCH_HIT_RESIZE_HAND_THRESHOLD = 100f * MajCtx.FRAME_LENGTH_SEC;
        /// <summary>
        /// 一组touch(hold)中，当存在end time大于start+此值的项时，重算一次手位.
        /// 后面参考<see cref="TOUCH_HIT_RESIZE_HAND_THRESHOLD"/>
        /// </summary>
        private const float TOUCH_HIT_SHORT_SPLIT_THRESHOLD = 5f * MajCtx.FRAME_LENGTH_SEC + (float)MajGeo.Epsilon;

        /// <summary>
        /// 双圆枚举的 3 点最小覆盖圆候选仅在 n ≤ 此值时启用。
        /// 3 点候选使双圆预合并从 O(n^5) 升到 O(n^7)，n=33 全 sensor 时会卡 ~3s，故大 n 降级为 1/2 点候选。
        /// </summary>
        private const int TWO_CIRCLE_3POINT_CANDIDATE_MAX_N = 16;

        /// <summary>DJAuto打星星的放手时机（判定后）</summary>
        public const float DJAUTO_SLIDE_RELEASE_DELAY_SEC = 6 * MajCtx.FRAME_LENGTH_SEC;

        /// <summary>手提前移动到 hit/swipe 的时间（线性插值窗口）</summary>
        public const float DJAUTO_HAND_PREADVANCE_SEC = 30f * MajCtx.FRAME_LENGTH_SEC;
        /// <summary>HIT滑动阈值：下一 data 与当前结束间隔 <= 此值, 则不 Off 直接移动</summary>
        public const float DJAUTO_HAND_CHAIN_SEC = 3f * MajCtx.FRAME_LENGTH_SEC;
        /// <summary>手位移速度上限（主圆直径 9.6 / 提前量 4*FRAME），超此距离来不及 -> Miss</summary>
        public const float DJAUTO_HAND_MAX_SPEED = 9.6f / (4f * MajCtx.FRAME_LENGTH_SEC);
        /// <summary>hit 结束到 swipe 开始允许预绑定的最大时间差。</summary>
        public const float DJAUTO_HIT_SWIPE_CHAIN_SEC = 0.1f;
        /// <summary>hit 位置到 swipe 起点允许预绑定的最大距离。</summary>
        public const float DJAUTO_HIT_SWIPE_CHAIN_DISTANCE = DJAUTO_HAND_MAX_RADIUS;

        // 存在减少late的隐秘指令
        private static float CurTime => TimeData.NoteTime + 0.01f;

        /// <summary>重置双手到初始位（半径 2.4 = 主圆一半，平行 x 轴直径两端），状态 Off，CurIdx=-1 无目标，FreeTime=-inf 确保初始可达。</summary>
        private void ResetDJAutoHands()
        {
            _djAutoHands[0] = new DJAutoHand { Pos = new float2(-2.4f, 0f), State = HandState.Off, CurIdx = -1, BindingSwipe = -1, FreeTime = float.NegativeInfinity };
            _djAutoHands[1] = new DJAutoHand { Pos = new float2(2.4f, 0f), State = HandState.Off, CurIdx = -1, BindingSwipe = -1, FreeTime = float.NegativeInfinity };
        }

        #region TouchCombine

        /// <summary>
        /// 本 timing 的 touch hit 双圆预合并：双手只有两只，用最多两个覆盖圆覆盖本 timing 尽量多的 touch 落点。
        /// 第一圆候选 = 每 1/2/3 点的最小覆盖圆；剩余点交给第二圆同样「尽量多管」（≤MAX 覆盖最多点的单圆）。
        /// 半径上限 DJAUTO_HAND_MAX_RADIUS、下限 DJAUTO_HAND_RADIUS；
        /// 选优：合计覆盖点数最多 -> max(r1,r2) 最小 -> |r1-r2| 最小。
        /// 超上限废弃；覆盖不到的点不生成 hit -> Miss 看命。计算由 Burst 直接把结果 Add 进 hits。
        /// </summary>
        private void CombineTouchHitsThisTiming()
        {
            var refs = _djAutoTouchHitsThisTiming;
            int n = refs.Length;
            if (n == 0) return;
            if (n == 1)
            {
                var r = refs[0];
                var sensor = r.Type == SimaiNoteType.TouchHold ? touchHolds[r.Index].sensor : touches[r.Index].sensor;
                var pos = MajPos.GetSensorJudgePos(sensor);
                hits.Add(new DJAutoHitData(pos, DJAUTO_HAND_RADIUS,
                    GetTouchStartTime(r, touches, touchHolds),
                    GetTouchEndTime(r, touches, touchHolds), -1));
                refs.Clear();
                return;
            }

            // 四圆：双圆 C1/C2 覆盖最多点，剩余点 S' 再算双圆 C3/C4（hashmap 缓存复用）。
            var four = ComputeFourCircle(refs.AsArray());
            CombineTouchHitsBurst(refs.AsArray(), touches, touchHolds, four, DJAUTO_HAND_RADIUS, DJAUTO_HAND_MAX_RADIUS, ref hits);
            refs.Clear();
        }

        /// <summary>四圆 = 两次双圆（缓存复用）：first 覆盖最多点，second 覆盖 first 之外的剩余点。</summary>
        private FourCircleBest ComputeFourCircle(NativeArray<DJAutoNoteRef> refs)
        {
            var first = GetOrComputeTwoCircle(refs);
            var four = new FourCircleBest { First = first };

            int n = refs.Length;
            var remaining = new NativeList<DJAutoNoteRef>(n, Allocator.Temp);
            for (int i = 0; i < n; i++)
            {
                var r = refs[i];
                var pos = GetTouchPos(r, touches, touchHolds);
                bool covered = (first.HasC1 && math.distance(pos, first.C1) <= first.R1 + MajGeo.Epsilon)
                            || (first.HasC2 && math.distance(pos, first.C2) <= first.R2 + MajGeo.Epsilon);
                if (!covered) remaining.Add(r);
            }
            if (remaining.Length > 0)
                four.Second = GetOrComputeTwoCircle(remaining.AsArray());
            remaining.Dispose();
            return four;
        }

        /// <summary>sensor 子集掩码池化：重复 touch 模式直接复用双圆手法，只算新的。</summary>
        private TwoCircleBest GetOrComputeTwoCircle(NativeArray<DJAutoNoteRef> refs)
        {
            ulong mask = ComputeSensorMask(refs, touches, touchHolds);
            if (_touchComboCache.TryGetValue(mask, out var best))
                return best;
            best = ComputeTwoCircle(refs, touches, touchHolds, DJAUTO_HAND_RADIUS, DJAUTO_HAND_MAX_RADIUS);
            _touchComboCache[mask] = best;
            return best;
        }

        [BurstCompile]
        private static float2 GetTouchPos(DJAutoNoteRef r, NativeList<TouchData> touches, NativeList<TouchHoldData> touchHolds)
        {
            var sensor = r.Type == SimaiNoteType.TouchHold ? touchHolds[r.Index].sensor : touches[r.Index].sensor;
            return MajPos.GetSensorJudgePos(sensor);
        }

        [BurstCompile]
        private static float GetTouchStartTime(DJAutoNoteRef r, NativeList<TouchData> touches, NativeList<TouchHoldData> touchHolds)
            => r.Type == SimaiNoteType.TouchHold ? touchHolds[r.Index].time : touches[r.Index].time;

        [BurstCompile]
        private static float GetTouchEndTime(DJAutoNoteRef r, NativeList<TouchData> touches, NativeList<TouchHoldData> touchHolds)
        {
            if (r.Type == SimaiNoteType.TouchHold)
            {
                var th = touchHolds[r.Index];
                return th.time + th.LastFor + DJAUTO_TOUCHHOLD_RELEASE_TIME_SEC;
            }
            var t = touches[r.Index];
            return t.time + DJAUTO_TOUCH_RELEASE_TIME_SEC;
        }

        [BurstCompile]
        private static ulong ComputeSensorMask(NativeArray<DJAutoNoteRef> refs, NativeList<TouchData> touches, NativeList<TouchHoldData> touchHolds)
        {
            ulong mask = 0;
            for (int i = 0; i < refs.Length; i++)
            {
                var r = refs[i];
                var sensor = r.Type == SimaiNoteType.TouchHold ? touchHolds[r.Index].sensor : touches[r.Index].sensor;
                mask |= 1UL << (int)sensor;
            }
            return mask;
        }

        [BurstCompile]
        private static void CombineTouchHitsBurst(
            NativeArray<DJAutoNoteRef> refs,
            NativeList<TouchData> touches, NativeList<TouchHoldData> touchHolds,
            FourCircleBest four,
            float handRadius,
            float maxRadius,
            ref NativeList<DJAutoHitData> hits)
        {
            int n = refs.Length;
            // 同一 timing 内所有 touch 的 start time 相同，直接取。
            float startMin = GetTouchStartTime(refs[0], touches, touchHolds);
            float maxEnd = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var end = GetTouchEndTime(refs[i], touches, touchHolds);
                if (end > maxEnd) maxEnd = end;
            }

            float split = startMin + TOUCH_HIT_SHORT_SPLIT_THRESHOLD;

            // 短段：四圆扫过（C1->C3 / C2->C4）或双圆静态（覆盖全部时）
            if (maxEnd <= split)
            {
                EmitShortSegment(ref hits, four, startMin, maxEnd);
                return;
            }

            // 长段：仅双圆静态（剩余 Miss）。第一段短按用 first.C1/C2。
            EmitCircles(ref hits, four.First, startMin, startMin + DJAUTO_TOUCH_RELEASE_TIME_SEC);

            // 长 touchhold（endtime > 阈值）按 endtime 升序排序。遍历中 gap >= TOUCH_HIT_RESIZE_HAND_THRESHOLD 断开：
            // 对「当前段及之后全部未结束的 hit」重新算一次双圆（= 前面遍历过的 + 后面未遍历的），
            // start 取上一段末（第一段为 split），end 取本段末（最后一个遍历到的最大 endtime）。如此往复到最后一个。
            var longs = new NativeList<DJAutoNoteRef>(n, Allocator.Temp);
            for (int i = 0; i < n; i++)
                if (GetTouchEndTime(refs[i], touches, touchHolds) > split) longs.Add(refs[i]);
            if (longs.Length > 0)
            {
                SortByEndTime(longs, touches, touchHolds);
                int m = longs.Length;
                int segIndex = 0;
                float segBegin = split;
                for (int i = 1; i <= m; i++)
                {
                    float endIm1 = GetTouchEndTime(longs[i - 1], touches, touchHolds);
                    bool breakHere = i == m
                        || (GetTouchEndTime(longs[i], touches, touchHolds) - endIm1) >= TOUCH_HIT_RESIZE_HAND_THRESHOLD;
                    if (!breakHere) continue;

                    int remLen = m - segIndex;
                    var rem = new NativeArray<DJAutoNoteRef>(remLen, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    for (int j = 0; j < remLen; j++) rem[j] = longs[segIndex + j];
                    var segBest = ComputeTwoCircle(rem, touches, touchHolds, handRadius, maxRadius);
                    EmitCircles(ref hits, segBest, segBegin, endIm1);
                    rem.Dispose();
                    segBegin = endIm1;
                    segIndex = i;
                }
            }
            longs.Dispose();
        }

        /// <summary>短段 emit：双圆覆盖全部则静态 hit，否则四圆扫过 = 每手两个端点 hit（零时长），updater 连按 On 移动实现扫过。</summary>
        [BurstCompile]
        private static void EmitShortSegment(
            ref NativeList<DJAutoHitData> hits,
            FourCircleBest four, float startTime, float endTime)
        {
            var first = four.First;
            var second = four.Second;
            // 双圆已覆盖全部 -> 静态 hit
            if (!second.HasC1)
            {
                EmitCircles(ref hits, first, startTime, endTime);
                return;
            }
            // 四圆扫过：C1->C3 一条轨迹（两端零时长 hit，中间靠连按 On 移动扫过覆盖剩余点）
            if (first.HasC1)
            {
                hits.Add(new DJAutoHitData(first.C1, first.R1, startTime, startTime + DJAUTO_TOUCH_RELEASE_TIME_SEC, -2));
                hits.Add(new DJAutoHitData(second.C1, second.R1, endTime, endTime + DJAUTO_TOUCH_RELEASE_TIME_SEC, -2));
            }
            // C2->C4（有 C4 则扫过，否则 C2 静态）
            if (first.HasC2)
            {
                if (second.HasC2)
                {
                    hits.Add(new DJAutoHitData(first.C2, first.R2, startTime, startTime + DJAUTO_TOUCH_RELEASE_TIME_SEC, -2));
                    hits.Add(new DJAutoHitData(second.C2, second.R2, endTime, endTime + DJAUTO_TOUCH_RELEASE_TIME_SEC, -2));
                }
                else
                    hits.Add(new DJAutoHitData(first.C2, first.R2, startTime, endTime, -2));
            }
        }

        /// <summary>对给定子集做双圆枚举（1/2/3 点候选 + 第二圆尽量多管），返回最优两圆，不写 hits。</summary>
        [BurstCompile]
        private static TwoCircleBest ComputeTwoCircle(
            NativeArray<DJAutoNoteRef> refs, NativeList<TouchData> touches, NativeList<TouchHoldData> touchHolds,
            float handRadius, float maxRadius)
        {
            int n = refs.Length;
            var best = new TwoCircleBest { Max = float.MaxValue, Diff = float.MaxValue };
            if (n == 0) return best;

            // 预计算各点 Pos（从 touch/touchhold sensor）
            var posArr = new NativeArray<float2>(n, Allocator.Temp);
            for (int i = 0; i < n; i++)
                posArr[i] = GetTouchPos(refs[i], touches, touchHolds);

            for (int i = 0; i < n; i++)
                ConsiderFirst(posArr, handRadius, maxRadius, new Circle { C = posArr[i], R = 0f }, ref best);
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    ConsiderFirst(posArr, handRadius, maxRadius, MinEnclosing2(posArr[i], posArr[j]), ref best);
            if (n <= TWO_CIRCLE_3POINT_CANDIDATE_MAX_N)
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++)
                        for (int k = j + 1; k < n; k++)
                            ConsiderFirst(posArr, handRadius, maxRadius, MinEnclosing3(posArr[i], posArr[j], posArr[k]), ref best);

            posArr.Dispose();
            return best;
        }

        /// <summary>把最优两圆以统一的 startTime/endTime 写入 hits（0/1/2 个）。</summary>
        [BurstCompile]
        private static void EmitCircles(ref NativeList<DJAutoHitData> hits, TwoCircleBest best, float startTime, float endTime)
        {
            if (best.HasC1)
                hits.Add(new DJAutoHitData(best.C1, best.R1, startTime, endTime, -2));
            if (best.HasC2)
                hits.Add(new DJAutoHitData(best.C2, best.R2, startTime, endTime, -2));
        }

        /// <summary>按 EndTime 升序插入排序（长 touchhold 通常很少，Burst 友好）。</summary>
        [BurstCompile]
        private static void SortByEndTime(NativeList<DJAutoNoteRef> list, NativeList<TouchData> touches, NativeList<TouchHoldData> touchHolds)
        {
            for (int i = 1; i < list.Length; i++)
            {
                var key = list[i];
                float keyEnd = GetTouchEndTime(key, touches, touchHolds);
                int j = i - 1;
                while (j >= 0 && GetTouchEndTime(list[j], touches, touchHolds) > keyEnd)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = key;
            }
        }

        [BurstCompile]
        private static void ConsiderFirst(
            NativeArray<float2> posArr, float handRadius, float maxRadius,
            Circle first, ref TwoCircleBest best)
        {
            float r1 = math.max(first.R, handRadius);
            if (r1 > maxRadius) return;

            float2 cc = first.C;
            int n = posArr.Length;
            var remaining = new NativeList<float2>(n, Allocator.Temp);
            for (int i = 0; i < n; i++)
                if (math.distance(posArr[i], cc) > r1 + MajGeo.Epsilon)
                    remaining.Add(posArr[i]);

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

            // 同覆盖点数时优先单圆(hasC2=false)：一只手大圆覆盖 优于 两只手小圆覆盖，
            // 避免双圆的 r2 hit 在双手状态机里抢不到手被忽略、其覆盖的 touch miss
            if (covered > best.Covered
                || (covered == best.Covered && !hasC2 && best.HasC2)
                || (covered == best.Covered && hasC2 == best.HasC2 && (maxR < best.Max
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

        /// <summary>四圆 = 两次双圆：First(C1/C2) 覆盖最多点，Second(C3/C4) 覆盖剩余；Second.HasC1=false 表示双圆已覆盖全部。</summary>
        [BurstCompile]
        private struct FourCircleBest
        {
            public TwoCircleBest First;
            public TwoCircleBest Second;
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

        /// <summary>指向 note 的引用，避免重复存 Pos/时长。Type 取 Touch/TouchHold。</summary>
        [BurstCompile]
        internal struct DJAutoNoteRef
        {
            public int Index;
            public SimaiNoteType Type;
        }

        #endregion


        internal enum HandState : byte { Off, Moving, On }

        /// <summary>一只手的状态：Pos 当前位置，State 状态机，CurIdx/CurKind 当前在线目标（-1 无），Moving 插值参数，ServeEnd On 最晚结束（连按累加）。两只手各自独立在线查找，互不关联。</summary>
        [BurstCompile]
        internal struct DJAutoHand
        {
            public float2 Pos;
            public HandState State;
            public int CurIdx;       // 当前目标在 hits/swipes 中的索引，-1 无
            public byte CurKind;     // 0=hit 1=swipe
            public int BindingSwipe; // 当前目标将要接续的 swipe 索引，-1 无

            public float MoveStart;
            public float MoveEnd;
            public float2 MoveFrom;
            public float2 MoveTo;

            public float Angle;      //当前相对原始方向的角度

            public float ServeEnd;   // On 状态最晚结束时间（多 data 连按等最后一个）
            public float FreeTime;   // Off 时(手空闲)的时刻
        }

        /// <summary>沿 swipe.Arrows 弧长参数化插值，镜像 Job 内同算法，供 Load 预计算绑定用。</summary>
        [BurstCompile]
        private static unsafe float2 ComputeSwipePosAt(SlidePose* arrows, int count, float startTime, float endTime, float time, float bindSkippableCNearestTime = -1)
        {
            if (count <= 1 || arrows == null) return float2.zero;
            var duration = endTime - startTime;
            var progress = duration > 0f ? math.saturate((time - startTime) / duration) : 0f;
            int idxLast = count - 1;
            var distance = progress * arrows[idxLast].L;
            int processIdx = 1;
            while (processIdx < idxLast && arrows[processIdx].L < distance) processIdx++;
            var p0 = arrows[processIdx - 1];
            var p1 = arrows[processIdx];
            var t = math.unlerp(p0.L, p1.L, distance);
            var pos = new float2(math.lerp(p0.X, p1.X, t), math.lerp(p0.Y, p1.Y, t));
            if (bindSkippableCNearestTime != -1)
            {
                if (math.abs(time - bindSkippableCNearestTime) <= 0.01f)
                {
                    // 蹭C区touch
                    return pos * 0.56f;
                }
            }
            return pos;
        }

        /// <summary>target 到 swipe 路径采样点的最近顶点（arrows 间隔短，免段内投影）：返回最近距离与 swipe 手到达该点的时刻。</summary>
        [BurstCompile]
        private static unsafe void SwipePathNearest(in DJAutoSwipeData swipe, float2 target,
            out float nearestDist, out float nearestTime)
        {
            var arrows = swipe.Arrows;
            var count = swipe.ArrowCount;
            var startTime = swipe.StartTime;
            var endTime = swipe.EndTime;

            nearestDist = float.MaxValue;
            nearestTime = startTime;
            if (count <= 1 || arrows == null) return;
            float totalL = arrows[count - 1].L;
            float duration = endTime - startTime;
            float bestL = 0f;
            for (int k = 0; k < count; k++)
            {
                var p = arrows[k];
                // 0.3f的神秘常数是取自最小判定区E的半径的再一半，
                // 因为实际上不需要完全摸到那个区的中点
                var d = math.distance(new float2(p.X, p.Y), target) - 0.3f;
                if (d < nearestDist)
                {
                    nearestDist = d;
                    bestL = p.L;
                }
            }
            nearestTime = totalL > 0f ? startTime + (bestL / totalL) * duration : startTime;
        }

        /// <summary>
        /// 标记可被 swipe 顺带覆盖的 hit：swipe 路径经过 hit 附近，且手到达最近点时 hit 仍在触发窗口内，
        /// 则绑定到该 swipe（设 BoundSwipe）并把 ExpandedRadius 抬到能覆盖它。运行时 FindEarliestTarget 跳过绑定 hit
        /// （不独立认领，让手认领 swipe），CurrentDataPos 用 ExpandedRadius 触发顺带覆盖。在所有 hit/swipe emit 完、Arrows 回填后调用。
        /// </summary>
        [BurstCompile]
        private unsafe void BindSkippableHitsBySwipe()
        {
            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit.BoundSwipe == -2) continue;  // 不允许被 swipe 顺带覆盖（tap/hold）
                float bestReq = float.MaxValue;
                int bestSwipe = -1;
                var perfectStart = hit.StartTime - NoteHelper.TOUCH_JUDGE_SEG_1ST_PERFECT_MSEC / 1000;
                var perfectEnd = hit.StartTime + NoteHelper.TOUCH_JUDGE_SEG_3RD_PERFECT_MSEC / 1000;
                for (int j = 0; j < swipes.Length; j++)
                {
                    var swipe = swipes[j];
                    if (swipe.ArrowCount <= 0 || swipe.Arrows == null) continue;
                    // 先做廉价时间筛选：若 swipe 整段与 hit 的 perfect 窗口没有交集，
                    // 不必遍历该 swipe 的全部 arrows。
                    if (swipe.EndTime < perfectStart || swipe.StartTime > perfectEnd) continue;
                    // 路径上离 hit 最近的点：swipe 手经过该点时若 hit 仍在 perfect 区间内，即可顺带覆盖
                    SwipePathNearest(swipe, hit.Pos, out float d, out float tArrive);
                    if (tArrive < perfectStart || tArrive > perfectEnd) continue;  // 到达时 touch 已经不在 perfect 区间了
                    if (math.all(hit.Pos == 0f) &&
                        d <= MajGeo.GroupBRadius + 0.01) // 可以蹭到C区touch
                    {
                        ref var h = ref hits.ElementRef(i);
                        h.BoundSwipe = j;
                        ref var s = ref swipes.ElementRef(j);
                        s.BindSkippableCNearestTime = tArrive;
                        s.Radius += 0.2f;
                        break;
                    }
                    if (d > DJAUTO_HAND_MAX_RADIUS) continue;  // 扩到最大半径也够不着
                    if (d < bestReq)
                    {
                        bestReq = d;
                        bestSwipe = j;
                    }
                }
                if (bestSwipe >= 0)
                {
                    ref var h = ref hits.ElementRef(i);
                    h.BoundSwipe = bestSwipe;
                    ref var s = ref swipes.ElementRef(bestSwipe);
                    s.Radius = math.max(s.Radius, bestReq);
                }
            }
        }

        /// <summary>
        /// DJAuto 双手自动演奏：两只手各自独立在线 FindNext（时序优先+空间 tiebreaker）。wifi 不排除他手，允许两手共享同一 wifi（side 按手 idx 派生 ±11.25°）。状态机 Off/Moving/On。
        /// On 时触发：世界坐标 hit/swipe 调 HandleWorldPosInput（sensor+红手）；外键 hit 调 HandleButtonInput（按键+红手）。Off/Moving 渲染灰色手圆不触发。
        /// </summary>
        [BurstCompile]
        private unsafe struct HitSwipeUpdateJob : IJob
        {
            public NativeArray<float> _djAutoMoveCurve;
            private float DJAutoMoveEvaluate(float t)
            {
                if (t < 0) return 0f; else if (t > 1) return 1f;
                return _djAutoMoveCurve[(int)(t * (DJAUTO_CURVE_RESOLUTION - 1))];
            }

            public NativeArray<DJAutoHand> hands;          // [0]=left [1]=right
            public NativeArray<DJAutoHitData> hits;
            public NativeArray<DJAutoSwipeData> swipes;

            private DJAutoHand _leftHand;
            private DJAutoHand _rightHand;

            public void Execute()
            {
                var time = CurTime;
                _leftHand = hands[0];
                _rightHand = hands[1];

                // 先各自更新状态，再统一按权重分配尚未占用的 data。
                bool leftCanChain = UpdateHandState(ref _leftHand, time);
                bool rightCanChain = UpdateHandState(ref _rightHand, time);
                FindNext(time, leftCanChain, rightCanChain);

                RenderAndTrigger(ref _leftHand, -1, time);
                RenderAndTrigger(ref _rightHand, +1, time);

                hands[0] = _leftHand;
                hands[1] = _rightHand;
            }

            /// <summary>只推进单手已锁目标的状态，不做目标分配。返回值表示本帧刚从 On 释放，可在统一分配时保持连按。</summary>
            private bool UpdateHandState(ref DJAutoHand hand, float time)
            {
                if (hand.CurIdx < 0) return false;

                float startTime = GetStartTime(ref hand);
                if (hand.State == HandState.On)
                {
                    bool released = hand.CurKind == 1
                        ? SwipeReleased(swipes[hand.CurIdx], time)
                        : time >= hand.ServeEnd;
                    if (!released) return false;

                    if (hand.BindingSwipe >= 0)
                    {
                        var next = GetBindingSwipeTarget(ref hand);
                        hand.CurIdx = hand.BindingSwipe;
                        hand.CurKind = 1;
                        hand.BindingSwipe = -1;
                        hand.State = HandState.On;
                        hand.MoveFrom = hand.Pos;
                        hand.MoveTo = next.EntryPos;
                        hand.MoveStart = hand.ServeEnd;
                        hand.MoveEnd = next.StartTime;
                        hand.ServeEnd = next.EndTime;
                        TryBindNextSwipe(ref hand);
                        return false;
                    }

                    hand.State = HandState.Off;
                    hand.FreeTime = hand.CurKind == 1 ? time : hand.ServeEnd;
                    hand.CurIdx = -1;
                    return true;
                }

                if (hand.State == HandState.Off)
                {
                    float moveStart = math.max(hand.FreeTime, startTime - DJAUTO_HAND_PREADVANCE_SEC);
                    if (time < moveStart) return false;
                    hand.State = HandState.Moving;
                    hand.MoveFrom = hand.Pos;
                    hand.MoveTo = GetEntryPos(ref hand);
                    hand.MoveStart = moveStart;
                    hand.MoveEnd = startTime;
                }

                if (hand.State == HandState.Moving)
                {
                    var t = math.saturate((time - hand.MoveStart) / math.max(hand.MoveEnd - hand.MoveStart, 1e-5f));
                    var pos1 = hand.Pos;
                    var pos2 = hand.Pos = math.lerp(hand.MoveFrom, hand.MoveTo, DJAutoMoveEvaluate(t));
                    if (time >= startTime)
                    {
                        hand.State = HandState.On;
                        hand.Pos = GetEntryPos(ref hand);
                        hand.ServeEnd = GetEndTime(ref hand);
                    }
                    hand.Angle += math.atan2(
                        pos1.x * pos2.y - pos1.y * pos2.x,
                        math.dot(pos1, pos2)
                    );
                }
                return false;
            }

            /// <summary>统一分配空闲手：每个候选只计算一次左右权重；已占用手权重恒为 -1。每轮认领一个最早候选，最多两轮。</summary>
            private void FindNext(float time, bool leftCanChain, bool rightCanChain)
            {
                for (int claimed = 0; claimed < 2; claimed++)
                {
                    var next = FindEarliestTarget(time, out int side);
                    if (!next.Valid) return;

                    if (side < 0)
                    {
                        ClaimTarget(ref _leftHand, next, time, leftCanChain);
                        TryBindNextSwipe(ref _leftHand);
                        leftCanChain = false;
                    }
                    else
                    {
                        ClaimTarget(ref _rightHand, next, time, rightCanChain);
                        TryBindNextSwipe(ref _rightHand);
                        rightCanChain = false;
                    }
                }
            }

            /// <summary>为一个候选设置当前目标；刚释放且间隔足够短时保持 On，继续沿途扫过，否则从 Off 按正常提前量移动。</summary>
            private void ClaimTarget(ref DJAutoHand hand, NextTarget next, float time, bool canChain)
            {
                SetCur(ref hand, next);
                if (canChain && next.StartTime >= hand.FreeTime && next.StartTime - hand.FreeTime <= DJAUTO_HAND_CHAIN_SEC)
                {
                    hand.State = HandState.On;
                    hand.MoveFrom = hand.Pos;
                    hand.MoveTo = next.EntryPos;
                    hand.MoveStart = hand.FreeTime;
                    hand.MoveEnd = next.StartTime;
                    hand.ServeEnd = next.EndTime;
                    return;
                }
                UpdateHandState(ref hand, time);
            }

            private void TryBindNextSwipe(ref DJAutoHand hand)
            {
                if (hand.CurIdx < 0 || hand.BindingSwipe >= 0) return;

                float2 sourcePos;
                float sourceEnd;
                if (hand.CurKind == 0)
                {
                    var hit = hits[hand.CurIdx];
                    sourcePos = hit.Pos;
                    sourceEnd = hit.EndTime;
                }
                else
                {
                    var swipe = swipes[hand.CurIdx];
                    if (swipe.ArrowCount <= 0 || swipe.Arrows == null) return;
                    var endArrow = swipe.Arrows[swipe.ArrowCount - 1];
                    sourcePos = new float2(endArrow.X, endArrow.Y);
                    sourceEnd = swipe.EndTime;
                }

                float bestAbsGap = float.MaxValue;
                int bestIdx = -1;
                for (int i = 0; i < swipes.Length; i++)
                {
                    if (IsClaimed(i, 1)) continue;
                    var swipe = swipes[i];
                    float gap = swipe.StartTime - sourceEnd;
                    float absGap = math.abs(gap);
                    if (absGap > DJAUTO_HIT_SWIPE_CHAIN_SEC) continue;
                    if (swipe.ArrowCount <= 0 || swipe.Arrows == null) continue;
                    var entryPos = new float2(swipe.Arrows[0].X, swipe.Arrows[0].Y);
                    if (math.distance(sourcePos, entryPos) > DJAUTO_HIT_SWIPE_CHAIN_DISTANCE) continue;
                    if (absGap < bestAbsGap)
                    {
                        bestAbsGap = absGap;
                        bestIdx = i;
                    }
                }

                if (bestIdx >= 0)
                {
                    hand.BindingSwipe = bestIdx;
                }
            }

            private NextTarget GetBindingSwipeTarget(ref DJAutoHand hand)
            {
                var swipe = swipes[hand.BindingSwipe];
                return new NextTarget
                {
                    Valid = true,
                    Index = hand.BindingSwipe,
                    Kind = 1,
                    StartTime = swipe.StartTime,
                    EndTime = swipe.EndTime,
                    EntryPos = swipe.ArrowCount > 0
                        ? new float2(swipe.Arrows[0].X, swipe.Arrows[0].Y)
                        : float2.zero
                };
            }

            /// <summary>渲染 + 触发：On 按当前 data 位置触发 sensor/按键并红手渲染，否则灰手。handSide 用于 wifi L/R 派生。</summary>
            private void RenderAndTrigger(ref DJAutoHand hand, int handSide, float time)
            {
                if (hand.State == HandState.On && hand.CurIdx >= 0)
                {
                    float st = GetStartTime(ref hand);
                    var pos = CurrentDataPos(ref hand, handSide, time, out float radius);
                    bool inTaskTime = time >= st;
                    // 连按 On 移动中（time < next.StartTime）：沿 MoveFrom->MoveTo 插值，沿途触发 = 扫过
                    if (!inTaskTime)
                    {
                        float t = math.saturate((time - hand.MoveStart) / math.max(hand.MoveEnd - hand.MoveStart, 1e-5f));
                        pos = math.lerp(hand.MoveFrom, hand.MoveTo, t);
                    }
                    hand.Pos = pos;  // 跟踪当前 data 位置，供连按 MoveFrom
                    InputData.HandleWorldPosInput(pos, radius);
                }
                else
                {
                    RenderHandOff(hand.Pos);
                }
            }

            /// <summary>下一个目标候选（统一 hit/swipe）。</summary>
            private struct NextTarget
            {
                public bool Valid;
                public int Index;
                public byte Kind;       // 0=hit 1=swipe
                public float StartTime;
                public float EndTime;
                public float2 EntryPos;
            }

            /// <summary>
            /// 从当前还未被手锁定的数据中选全局最早候选。同一 StartTime 再按将要认领它的手的距离决定。
            /// 每个候选只在这里同时计算一次左右权重；忙手的权重为 -1。
            /// </summary>
            private NextTarget FindEarliestTarget(float time, out int side)
            {
                var best = new NextTarget();
                side = 0;
                float bestStart = float.MaxValue;
                float bestDist = float.MaxValue;

                for (int i = 0; i < hits.Length; i++)
                {
                    var hit = hits[i];
                    // 该 hit 已绑定到某 swipe，将由 swipe 扩大半径顺带覆盖，不独立认领（让手去认领 swipe）
                    if (hit.BoundSwipe >= 0) continue;
                    if (IsClaimed(i, 0) ||
                        time < hit.StartTime - DJAUTO_HAND_PREADVANCE_SEC ||
                        time > hit.EndTime) continue;
                    float2 entryPos = hit.Pos;
                    int targetSide = SelectHand(entryPos, hit.StartTime, out float targetDist);
                    if (targetSide == 0) continue;
                    if (hit.StartTime < bestStart || (hit.StartTime == bestStart && targetDist < bestDist))
                    {
                        bestStart = hit.StartTime;
                        bestDist = targetDist;
                        side = targetSide;
                        best = new NextTarget { Valid = true, Index = i, Kind = 0, StartTime = hit.StartTime, EndTime = hit.EndTime, EntryPos = entryPos };
                    }
                }

                for (int i = 0; i < swipes.Length; i++)
                {
                    var swipe = swipes[i];
                    // 普通 swipe 被任一手锁定后不可重领；wifi 允许另一只空闲手继续认领。
                    if ((!swipe.IsWifi && IsClaimed(i, 1)) ||
                        time < swipe.StartTime - DJAUTO_HAND_PREADVANCE_SEC ||
                        SwipeReleased(swipe, time)) continue;
                    float2 entryPos = swipe.ArrowCount > 0 ? new float2(swipe.Arrows[0].X, swipe.Arrows[0].Y) : float2.zero;
                    int targetSide = SelectHand(entryPos, swipe.StartTime, out float targetDist);
                    if (targetSide == 0) continue;
                    // 同一时刻仍有 hit 时，先把空手分给 hit；否则预绑定的 wifi
                    // 会作为共享 swipe 被另一只手抢走，挤掉同 timing 的另一颗 hit。
                    if (swipe.StartTime < bestStart)
                    {
                        bestStart = swipe.StartTime;
                        bestDist = targetDist;
                        side = targetSide;
                        best = new NextTarget { Valid = true, Index = i, Kind = 1, StartTime = swipe.StartTime, EndTime = swipe.EndTime, EntryPos = entryPos };
                    }
                }

                return best;
            }

            private readonly bool IsClaimed(int index, byte kind)
                => (_leftHand.CurIdx == index && _leftHand.CurKind == kind)
                || (_rightHand.CurIdx == index && _rightHand.CurKind == kind)
                || (kind == 1 && (_leftHand.BindingSwipe == index
                    || _rightHand.BindingSwipe == index));

            /// <summary>同时衡量左右手，空闲且可达才有权重；相等时稳定地优先右手。</summary>
            private int SelectHand(float2 entryPos, float startTime, out float selectedDist)
            {
                float leftDist = math.distance(_leftHand.Pos, entryPos);
                float rightDist = math.distance(_rightHand.Pos, entryPos);
                float leftWeight = GetWeight(_leftHand, startTime, leftDist);
                float rightWeight = GetWeight(_rightHand, startTime, rightDist);
                if (leftWeight < 0f && rightWeight < 0f)
                {
                    selectedDist = 0f;
                    return 0;
                }
                if (leftWeight > rightWeight)
                {
                    selectedDist = leftDist;
                    return -1;
                }
                selectedDist = rightDist;
                return +1;
            }

            /// <summary>
            /// 已占用手不可参与本轮分配，直接返回 -1。
            /// 手对目标的权重 = 角度(不绕优先,0.4) + 距离(近优先,0.4) + 时间(早优先,0.3)。
            /// </summary>
            private static float GetWeight(in DJAutoHand hand, float startTime, float dist)
            {
                var time = CurTime;
                if (hand.CurIdx >= 0 || time < hand.FreeTime || !ReachableJob(hand.FreeTime, startTime, dist)) return -1f;
                float w = 0f;

                // 不绕的优先，手绕麻花越劲，note离你越远
                w += 1f - math.pow(math.saturate(hand.Angle / math.PI2), 3) * 0.4f;

                // 近的优先；dist=0 时为 1，dist=2*MajPos.MAIN_RADIUS 时为 0.4
                w += 1f - math.pow(math.saturate(dist / (2f * MajPos.MAIN_RADIUS)), 3) * 0.4f;

                // 早的优先；间隔=0 时为 1，间隔=2*DJAUTO_HAND_PREADVANCE_SEC 时为 0.3。
                // saturate 防止 FreeTime=-inf(初始) 或大间隔时 (startTime-FreeTime) 溢出 +inf 把权重打成 -inf，
                // 否则 SelectHand 的 <0 判定会把两手都误判不可用 -> FindNext 不分配 -> 手停在初始位
                w += 1f - math.pow(math.saturate((startTime - hand.FreeTime) / (2f * DJAUTO_HAND_PREADVANCE_SEC)), 3) * 0.3f;
                return w;
            }

            /// <summary>手在 freeTime 空闲，需 PREADVANCE 提前量；位移时间 = dist/MAX_SPEED，超可用时间则不可达。</summary>
            private static bool ReachableJob(float freeTime, float startTime, float dist)
            {
                float moveStart = math.max(freeTime, startTime - DJAUTO_HAND_PREADVANCE_SEC);
                float avail = startTime - moveStart;
                return avail > 0f && dist <= avail * DJAUTO_HAND_MAX_SPEED;
            }

            private void SetCur(ref DJAutoHand hand, in NextTarget next)
            {
                hand.CurIdx = next.Index;
                hand.CurKind = next.Kind;
            }

            private float GetStartTime(ref DJAutoHand hand)
                => hand.CurKind == 0 ? hits[hand.CurIdx].StartTime : swipes[hand.CurIdx].StartTime;
            private float GetEndTime(ref DJAutoHand hand)
                => hand.CurKind == 0 ? hits[hand.CurIdx].EndTime : swipes[hand.CurIdx].EndTime;

            /// <summary>swipe 是否已到放手时机：镜像 SlideUpdateJob 动态放手（isEnd/isSlideEnd 或 已判定+延迟），用于 On 结束与窗口上界。</summary>
            private static bool SwipeReleased(DJAutoSwipeData swipe, float time)
            {
                var slide = swipe.BindingSlide;
                return slide->isEnd || slide->isSlideEnd || (slide->isJudged && time > slide->judgeTime + DJAUTO_SLIDE_RELEASE_DELAY_SEC);
            }
            private float2 GetEntryPos(ref DJAutoHand hand)
            {
                if (hand.CurKind == 0)
                {
                    var hit = hits[hand.CurIdx];
                    return hit.Pos;
                }
                var swipe = swipes[hand.CurIdx];
                return swipe.ArrowCount > 0 ? new float2(swipe.Arrows[0].X, swipe.Arrows[0].Y) : float2.zero;
            }

            /// <summary>按 CurKind 算当前目标位置 + 半径。wifi side 由 handSide（左=-1，右=+1）派生。</summary>
            private float2 CurrentDataPos(ref DJAutoHand hand, int handSide, float time, out float radius)
            {
                if (hand.CurKind == 0)
                {
                    var hit = hits[hand.CurIdx];
                    radius = hit.Radius;
                    return hit.Pos;
                }
                var swipe = swipes[hand.CurIdx];
                radius = swipe.Radius;
                if (swipe.IsWifi)
                {
                    return WifiSwipePos(swipe, time, handSide);
                }
                return ComputeSwipePos(swipe, time);
            }

            /// <summary>
            /// 沿 swipe.Arrows 做弧长参数化插值，镜像 SlideUpdateJob 的非 wifi 星星位置算法。
            /// wifi 时此为基础位置（C 支），WifiSwipePos 再绕起点 ±11.25° 派生 L/R。
            /// </summary>
            private static float2 ComputeSwipePos(DJAutoSwipeData swipe, float time)
                => ComputeSwipePosAt(swipe.Arrows, swipe.ArrowCount, swipe.StartTime, swipe.EndTime, time, swipe.BindSkippableCNearestTime);

            /// <summary>wifi 双手：基础位置(C 支)相对起点偏移绕起点旋转 ±11.25°（L/R 支与 C 支中间）。side=-1 L, +1 R。</summary>
            private static float2 WifiSwipePos(DJAutoSwipeData swipe, float time, int side)
            {
                var posC = ComputeSwipePos(swipe, time);
                var arrows = swipe.Arrows;
                var startPos = new float2(arrows[0].X, arrows[0].Y);
                var offset = posC - startPos;
                var rad = math.radians(11.25f);
                var cos = math.cos(rad);
                var sin = math.sin(rad);
                return startPos + new float2(
                    offset.x * cos - side * offset.y * sin,
                    side * offset.x * sin + offset.y * cos);
            }

            /// <summary>Off/Moving 渲染灰色手圆（不触发 sensor）。</summary>
            private static void RenderHandOff(float2 pos)
            {
                if (!InputData.ShowHand) return;
                var idx = Interlocked.Increment(ref *InputData.HitWriteCountPtr) - 1;
                InputData.hitRender[idx] = new HitRenderData
                {
                    pos = pos,
                    radius = DJAUTO_HAND_RADIUS,
                    color = new float4(0.6f, 0.6f, 0.6f, 0.5f)
                };
            }

            /// <summary>On 状态红手渲染（外键 hit 走 HandleButtonInput 不触发 sensor，单独渲染红手圆）。</summary>
            private static void RenderHandOn(float2 pos, float radius)
            {
                if (!InputData.ShowHand) return;
                var idx = Interlocked.Increment(ref *InputData.HitWriteCountPtr) - 1;
                InputData.hitRender[idx] = new HitRenderData
                {
                    pos = pos,
                    radius = radius,
                    color = new float4(1f, 0f, 0f, 0.75f)
                };
            }
        }
    }
}
