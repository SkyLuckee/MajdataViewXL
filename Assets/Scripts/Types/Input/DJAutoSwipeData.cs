using MajdataViewX.Notes.NoteDatas;
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
        public float Radius;                // radius后面可能为了蹭touch要改一下
        public readonly float StartTime;
        public readonly SlideData* BindingSlide;
        public readonly float EndTime;
        /// <summary>
        /// 标记这个swipe是否是由wifi产生的，如果是，则需要±22.5度的偏移变成滑动的双手
        /// </summary>
        public readonly bool IsWifi;
        /// <summary>
        /// 标记这个swipe是准备去蹭C区touch的，别的区算法会解决，C区距离实在太远，单纯扩大半径已经不合适了
        /// -1为没绑上
        /// </summary>
        public float BindSkippableCNearestTime;
        public DJAutoSwipeData(int arrowsOffset, int arrowCount, float radius, float startTime, SlideData* bindingSlide, float endTime, bool isWifi)
        {
            this = default;
            ArrowsOffset = arrowsOffset;
            ArrowCount = arrowCount;
            Radius = radius;
            StartTime = startTime;
            BindingSlide = bindingSlide;
            EndTime = endTime;
            IsWifi = isWifi;
            BindSkippableCNearestTime = -1;
        }
    }
}
