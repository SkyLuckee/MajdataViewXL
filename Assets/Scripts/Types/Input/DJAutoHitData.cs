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
        public readonly bool CanSkipBySwiped;

        public DJAutoHitData(float2 pos, float radius, float startTime, float endTime, bool canSkipBySwiped)
        {
            this = default;
            Pos = pos;
            Radius = radius;
            ButtonPos = -1; // 世界坐标 hit：用 -1 标记，与外键 0~7 区分（this=default 会置 0，与 A1 冲突）
            StartTime = startTime;
            EndTime = endTime;
            CanSkipBySwiped = canSkipBySwiped;
        }
        public DJAutoHitData(int buttonPos, float startTime, float endTime, bool canSkipBySwiped)
        {
            this = default;
            ButtonPos = buttonPos;
            StartTime = startTime;
            EndTime = endTime;
            CanSkipBySwiped = canSkipBySwiped;
        }
    }
}
