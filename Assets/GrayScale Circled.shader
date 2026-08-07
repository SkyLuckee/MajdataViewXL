Shader "Custom/GreyscaleCircled" 
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Center ("Center (World XY)", Vector) = (0,0,0,0)
        _Radius ("Radius", Float) = 2.0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass 
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float3 worldPos : TEXCOORD1;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Center;
            float _Radius;

            v2f vert(appdata v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Circle mask
                float dist = distance(i.worldPos.xy, _Center.xy);
                if (dist > _Radius)
                    discard;

                // Greyscale conversion
                fixed4 col = tex2D(_MainTex, i.uv);
                float grey = dot(col.rgb, float3(0.299, 0.587, 0.114));

                // Use ONLY texture alpha, ignore material alpha
                return fixed4(grey, grey, grey, col.a);
            }
            ENDCG
        }        
    }
    FallBack "Diffuse"
}