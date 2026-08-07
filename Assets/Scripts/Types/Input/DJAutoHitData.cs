using Unity.Mathematics;

namespace MajdataViewX.Types.Input
{
    internal readonly struct DJAutoHitData
    {
        /// <summary>
        /// 世界坐标
        /// </summary>
        public readonly float2 Pos;
        public readonly float Radius;
        /// <summary>
        /// 按键索引，范围0~7对应1~8号键，当被赋值时Pos被忽略
        /// </summary>
        public readonly int ButtonPos;

        public readonly float StartTime;
        public readonly float EndTime;
        public readonly bool CanBeCombined;
        public readonly bool CanSkipBySwiped;

        public DJAutoHitData(float2 pos, float radius, float startTime, float endTime, bool canBeCombined, bool canSkipBySwiped)
        {
            this = default;
            Pos = pos;
            Radius = radius;
            StartTime = startTime;
            EndTime = endTime;
            CanBeCombined = canBeCombined;
            CanSkipBySwiped = canSkipBySwiped;
        }
        public DJAutoHitData(int buttonPos, float startTime, float endTime, bool canBeCombined, bool canSkipBySwiped)
        {
            this = default;
            ButtonPos = buttonPos;
            StartTime = startTime;
            EndTime = endTime;
            CanBeCombined = canBeCombined;
            CanSkipBySwiped = canSkipBySwiped;
        }
    }
}
