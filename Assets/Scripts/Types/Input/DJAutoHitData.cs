using Unity.Mathematics;

namespace MajdataViewX.Types.Input
{
    internal struct DJAutoHitData
    {
        /// <summary>
        /// 世界坐标(利用大于4.8半径的位置按下判定为按下外键的特性支持外键打击)
        /// </summary>
        public readonly float2 Pos;
        public readonly float Radius;

        public readonly float StartTime;
        public readonly float EndTime;
        /// <summary>-2=不允许被 swipe 顺带覆盖（tap/hold 恒为 -2）；-1=允许但未找到匹配 swipe；>=0=绑定的 swipe 索引。
        /// 绑定后该 hit 由 swipe 执行时扩大半径顺带覆盖，FindEarliestTarget 不再独立认领它。</summary>
        public int BoundSwipe;

        public DJAutoHitData(float2 pos, float radius, float startTime, float endTime, int boundSwipe)
        {
            this = default;
            Pos = pos;
            Radius = radius;
            StartTime = startTime;
            EndTime = endTime;
            BoundSwipe = boundSwipe;
        }
    }
}
