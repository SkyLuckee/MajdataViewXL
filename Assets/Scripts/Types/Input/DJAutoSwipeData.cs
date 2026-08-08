using MajdataViewX.Notes.SlideUtils;

namespace MajdataViewX.Types.Input
{
    /// <summary>
    /// <p>直接复用 slide 已烘焙好的 <c>slideArrows</c>，</p>
    /// </summary>
    internal unsafe struct DJAutoSwipeData
    {
        public readonly int ArrowsOffset;   // slidePosePool 内偏移，malloc 后用于回填 Arrows
        public readonly int ArrowCount;
        public SlidePose* Arrows;           // slidePosePool + ArrowsOffset，Load 末尾回填
        public readonly float Radius;
        public readonly float StartTime;
        public readonly float ReleaseTime;  // 放手时机
        public readonly float EndTime;
        /// <summary>
        /// 标记这个swipe是否是由wifi产生的，如果是，则需要±22.5度的偏移变成滑动的双手
        /// </summary>
        public readonly bool IsWifi;
        public DJAutoSwipeData(int arrowsOffset, int arrowCount, float radius, float startTime, float releaseTime, float endTime, bool isWifi)
        {
            ArrowsOffset = arrowsOffset;
            Arrows = default;
            ArrowCount = arrowCount;
            Radius = radius;
            StartTime = startTime;
            ReleaseTime = releaseTime;
            EndTime = endTime;
            IsWifi = isWifi;
        }
    }
}
