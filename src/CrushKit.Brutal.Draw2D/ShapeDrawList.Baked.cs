using Brutal.Numerics;

namespace CrushKit.Brutal.Draw2D;

public sealed partial class ShapeDrawList
{
    private int bakedVertices,bakedIndices;
    internal PixelRect BakedClip=>clips[^1];
    internal ReadOnlySpan<ShapeCommand> BakedCommands=>commands.AsSpan(0,cc);
    internal bool PrepareBaked(float2 position,LinearColor color)
    {
        Start(color);_=Input(position);return !NoOutput(color);
    }
    internal bool TryBakedDisc(BakedMeshCache cache,float2 center,float radius,LinearColor color,
        out BakedShapeMesh? mesh,out MeshInstance instance, CircleDetail? detail = null)
    {
        Start(color);_=Input(center);Nonnegative(radius);mesh=null;instance=default;
        if(NoOutput(color) || radius==0)return true;
        int n=DiscResolution(radius,detail);
        mesh=cache.Find(-n) ?? BakeDisc(cache,n);
        if(mesh is null)return false;
        if(TryBakedInstance(mesh,center,radius,0,color,out instance,detail?.Mode != CircleDetailMode.FixedSideCount))return true;
        mesh=null;return false;
    }
    private static BakedShapeMesh? BakeDisc(BakedMeshCache cache,int n)=>cache.Get(-n,BakedShapeMesh.DiscCharge(n),()=>BakedShapeMesh.Disc(n));
    internal void CommitBaked(BakedShapeMesh mesh) { bakedVertices+=mesh.VertexCount;bakedIndices+=mesh.IndexCount; }
    internal bool TryBakedInstance(BakedShapeMesh mesh,float2 position,float radius,double sourceEndpointError,LinearColor color,
        out MeshInstance instance,bool automaticCurve = true)
    {
        instance=default;
        if(!double.IsFinite(sourceEndpointError) || sourceEndpointError<0)throw new ArgumentOutOfRangeException(nameof(sourceEndpointError));
        if(!world.Equals(float3x2.Identity) || local.M12!=0 || local.M21!=0 || !(local.M11>0) || local.M11!=local.M22)return false;
        var origin=Pixel(new(position.X,position.Y));double scale=(double)radius*local.M11;
        // Do not infer CPU arithmetic safety from a small result after large cancellation.
        double intermediates=Math.Max(Math.Abs((double)position.X)*local.M11+Math.Abs((double)local.M31),
            Math.Abs((double)position.Y)*local.M11+Math.Abs((double)local.M32))+scale*mesh.Bound;
        if(!double.IsFinite(intermediates) || Math.BitIncrement(intermediates)>8191)return false;
        float2 packedOrigin=new((float)origin.X,(float)origin.Y);float packedScale=(float)scale;
        if(!BakedPrecision.TryBound(mesh,origin.X,origin.Y,scale,packedOrigin,packedScale,sourceEndpointError*local.M11,width,height,out _,automaticCurve))return false;
        if(mesh.VertexCount>maxVertices-vc-bakedVertices || mesh.IndexCount>maxIndices-ic-bakedIndices)
            throw new InvalidOperationException("Frame geometry budget exceeded.");
        instance=new(packedOrigin,new(packedScale,0),new(0,packedScale),color.Premultiplied);return true;
    }
}

/// <summary>Sufficient (not necessary) scalar bounds for the narrow uniform-positive instance path.</summary>
internal static class BakedPrecision
{
    private const double U=1.0/(1<<24);
    // All possible flushed binary32 subnormal contributions, per coordinate, over both shader paths.
    private const double Underflow=64*8192*1.1754943508222875e-38;
    internal readonly record struct Bounds(double ShaderError,double ReferenceError,double DistanceBound,double AreaMargin);
    private static double Up(double x)=>Math.BitIncrement(x);
    private static double Add(double a,double b)=>Up(a+b);
    private static double Mul(double a,double b)=>Up(a*b);
    private static double LowerMul(double a,double b)=>Math.BitDecrement(a*b);
    internal static bool TryBound(BakedShapeMesh mesh,double x,double y,double scale,float2 origin,float packedScale,
        double sourceError,int width,int height,out Bounds bounds,bool automaticCurve = true)
    {
        bounds=default;
        // Input FTZ of a local subnormal could be amplified by an arbitrarily large normal scale.
        // Keep the accepted normal-operand proof unchanged; these meshes use ordinary reference fallback.
        if(mesh.HasSubnormalCoordinates)return false;
        if(!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(scale) || !(scale>0) || !float.IsNormal(packedScale) || packedScale<=0 || width>8192 || height>8192)return false;
        double t=Up(Math.Max(Math.Abs(x),Math.Abs(y))), b=mesh.Bound;
        double m=Add(t,Mul(scale,b));
        if(m>8192)return false;
        double pack=Add(Up(Math.Max(Math.Abs((double)origin.X-x),Math.Abs((double)origin.Y-y))),Mul(Up(Math.Abs((double)packedScale-scale)),b));
        // Separate mul/add dominates FMA. Budget 16 binary32 relative-error units on pixel magnitude,
        // including GLSL division allowance (not assumed correctly rounded); the affine NDC offset
        // contributes at most 4*u*extent in pixel units. Concrete derivation is a native-review gate.
        double gamma=Up(16*U/(1-16*U));
        double viewportOffset=Mul(4*U,Math.Max(width,height));
        double shader=Add(Add(pack,Add(Mul(gamma,Add(m,pack)),viewportOffset)),Underflow);
        // CPU reference: <=8 double operations then one float conversion, followed by NDC stages.
        // Include local endpoint baking error and game's pre-transform endpoint conversion discrepancy.
        double sourceOffset=Add(Mul(scale,mesh.SourceError),sourceError);
        // Caller bounds the SUM of absolute CPU intermediates (not merely the final origin) by 8192.
        double cpuDoubleError=Mul(16*2.220446049250313e-16,8192);
        double referenceOffset=Add(sourceOffset,cpuDoubleError);
        double referenceCast=Mul(U,Add(m,referenceOffset));
        double referenceMagnitude=Add(Add(m,referenceOffset),referenceCast);
        double reference=Add(Add(Add(referenceOffset,referenceCast),Add(Mul(gamma,referenceMagnitude),viewportOffset)),Underflow);
        double e=Up(Math.Max(shader,reference));double distance=Mul(1.4142135623730952,Add(shader,reference));
        double margin=Add(Mul(Mul(Mul(2,e),scale),mesh.MaximumEdgeSum),Mul(Mul(8,e),e));
        bounds=new(shader,reference,distance,margin);
        if(!double.IsFinite(distance) || distance>0.01 || Add(m,e)>1_048_576 || !(LowerMul(LowerMul(scale,scale),mesh.MinimumArea)>margin))return false;
        // Existing curve policy reserves 0.09px for quantization. Never add to its 0.25px allowance.
        if(mesh.IsDisc) return e<=0.09 && (!automaticCurve || Add(Mul(scale,mesh.UnitCurveError),e)<=0.25);
        // All compound boundaries are axis-aligned; identical local lines have identical shader evaluation.
        // Strict line ordering preserves every gap/cutout under the admitted positive uniform transform.
        return LowerMul(scale,mesh.FeatureSeparation)>Mul(2,e);
    }
}
