using System.Text.Json;
using System.Text.Json.Serialization;

namespace PileCage;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoverBasis { OutermostSurface, LongitudinalSurface }
public sealed class CageInput
{
    public string CageId { get; set; } = "DEMO-001";
    public string CageGuid { get; set; } = "";
    public string SourceKind { get; set; } = "DEMONSTRATION";
    public bool EngineeringConfirmed { get; set; } = true;
    public double Diameter { get; set; } = 1000;
    public double Height { get; set; } = 3000;
    public double Cover { get; set; } = 50;
    public CoverBasis CoverBasis { get; set; } = CoverBasis.OutermostSurface;
    public int MainCount { get; set; } = 16;
    public double MainDiameter { get; set; } = 20;
    public double MainStart { get; set; } = 100;
    public double MainEnd { get; set; } = 2900;
    public double SpiralDiameter { get; set; } = 8;
    public double SpiralStart { get; set; } = 100;
    public List<ZoneInput> Zones { get; set; } = [new() { Id="Z1",Length=1200,Pitch=100 },new() { Id="Z2",Length=1500,Pitch=200 }];
    public bool RingsEnabled { get; set; } = true;
    public double RingDiameter { get; set; } = 12;
    public double RingSpacing { get; set; } = 1000;
    public double RingStart { get; set; } = 200;
    public double RingEnd { get; set; } = 2800;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long HostId { get; set; }
    public string HostDocumentUniqueId { get; set; } = "";
    public long MainTypeId { get; set; }
    public long SpiralTypeId { get; set; }
    public long RingTypeId { get; set; }
    public bool CreateHost { get; set; } = true;
    public double Tolerance { get; set; } = 0.5;
    public List<string> Pending { get; set; } = [];
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
    public CageInput Clone() => JsonSerializer.Deserialize<CageInput>(JsonSerializer.Serialize(this,Files.Json),Files.Json)!;
}
public sealed class ZoneInput { public string Id {get;set;}="Z1"; public double Length {get;set;}=1000; public double Pitch {get;set;}=200; }
public sealed record Point3(double X,double Y,double Z);
public sealed class HelixPiece { public double Start {get;set;} public double End {get;set;} public double Pitch {get;set;} public double StartAngle {get;set;} }
public sealed class BarPlan
{
    public string Id {get;set;}=""; public string Kind {get;set;}="";
    public double Diameter {get;set;} public double Radius {get;set;} public double Start {get;set;} public double End {get;set;} public double Length {get;set;}
    public List<Point3> Points {get;set;}=[]; public List<HelixPiece> Pieces {get;set;}=[];
}
public sealed class CagePlan {public double MainRadius {get;set;} public double SpiralRadius {get;set;} public double RingRadius {get;set;} public List<BarPlan> Bars {get;set;}=[];}
public static class Files
{
    public static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};
    public static string Root {get;set;}=FindRoot();
    public static string ParameterDirectory {get;set;}=System.IO.Path.Combine(Root,"parameters");
    private static string FindRoot()
    {
        var directory = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(typeof(Files).Assembly.Location)!);
        while (directory != null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "pilecage.workspace")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("请保留插件 DLL 与 pilecage.workspace 的目录关系。");
    }
}
public static class CagePlanner
{
    public static (double Main,double Spiral,double Ring) Radii(CageInput p)
    {
        double m=p.CoverBasis==CoverBasis.OutermostSurface?p.Diameter/2-p.Cover-p.SpiralDiameter-p.MainDiameter/2:p.Diameter/2-p.Cover-p.MainDiameter/2;
        return(m,m+p.MainDiameter/2+p.SpiralDiameter/2,m-p.MainDiameter/2-p.RingDiameter/2);
    }
    public static int RingCount(CageInput p)=>!p.RingsEnabled||!double.IsFinite(p.RingSpacing)||!double.IsFinite(p.RingEnd)||!double.IsFinite(p.RingStart)||p.RingSpacing<=0||p.RingEnd<p.RingStart?0:(int)Math.Min(int.MaxValue,Math.Floor((p.RingEnd-p.RingStart)/p.RingSpacing+1e-9)+1);
    public static List<string> Validate(CageInput p)
    {
        var e=new List<string>();
        if(string.IsNullOrWhiteSpace(p.CageId))e.Add("钢筋笼编号不能为空。");
        if(!p.EngineeringConfirmed||p.Pending==null||p.Pending.Count>0||string.IsNullOrWhiteSpace(p.SourceKind)||p.SourceKind.Contains("PENDING",StringComparison.OrdinalIgnoreCase))e.Add("工程参数尚有待确认字段，不能生成；请先完成来源与基准确认。");
        if(!Enum.IsDefined(p.CoverBasis))e.Add("保护层测量基准无效，请明确选择主筋表面或最外层表面。");
        var numbers=new[]{p.Diameter,p.Height,p.Cover,p.MainDiameter,p.MainStart,p.MainEnd,p.SpiralDiameter,p.SpiralStart,p.X,p.Y,p.Z,p.Tolerance};
        if(numbers.Any(v=>!double.IsFinite(v))){e.Add("输入必须为有限数值。");return e;}
        if(p.Diameter<=0||p.Height<=0||p.Cover<=0||p.MainDiameter<=0||p.SpiralDiameter<=0)e.Add("桩径、桩长、保护层与钢筋直径必须大于 0。");
        if(p.MainCount<1||p.MainCount>500)e.Add("主筋数量须为 1～500 的整数（功能容量限制）。");
        if(p.Tolerance<=0||p.Tolerance>0.5)e.Add("几何容差须在 (0, 0.5] mm；不通过放宽门槛掩盖误差。");
        if(p.MainStart<p.MainDiameter/2||p.MainEnd>p.Height-p.MainDiameter/2||p.MainEnd<=p.MainStart)e.Add("主筋起止面须位于桩底 z=0 与桩顶 z=H 之间，并留出钢筋半径。");
        if(p.Zones==null||p.Zones.Count<1||p.Zones.Count>100||p.Zones.Any(z=>z==null)){e.Add("配筋分区须为 1～100 个有效区段。");return e;}
        if(p.Zones.Any(z=>!double.IsFinite(z.Length)||!double.IsFinite(z.Pitch)||z.Length<=0||z.Pitch<=p.SpiralDiameter))e.Add("各区段长度须大于 0，螺距须大于螺旋筋直径。");
        if(p.Zones.Select(z=>z.Id).Distinct().Count()!=p.Zones.Count||p.Zones.Any(z=>string.IsNullOrWhiteSpace(z.Id)))e.Add("区段编号须唯一且非空。");
        if(p.SpiralStart<p.SpiralDiameter/2||p.SpiralStart+p.Zones.Sum(z=>z.Length)>p.Height-p.SpiralDiameter/2)e.Add("螺旋筋范围超出桩体。");
        if(p.Zones.Where(z=>z.Pitch>0).Sum(z=>z.Length/z.Pitch)>2000)e.Add("螺旋超过 2000 圈，超出第一版建模容量。");
        var r=Radii(p);
        if(r.Main<=0||r.Spiral+p.SpiralDiameter/2>=p.Diameter/2)e.Add("保护层和钢筋直径组合无法形成有效的内外位置。");
        if(p.MainCount>1&&2*r.Main*Math.Sin(Math.PI/p.MainCount)<=p.MainDiameter)e.Add("主筋周向实体重叠，无法形成有效钢筋笼。");
        if(p.RingsEnabled){if(new[]{p.RingDiameter,p.RingSpacing,p.RingStart,p.RingEnd}.Any(v=>!double.IsFinite(v))||p.RingDiameter<=0||p.RingSpacing<=p.RingDiameter||p.RingStart<p.RingDiameter/2||p.RingEnd>p.Height-p.RingDiameter/2||p.RingEnd<p.RingStart||r.Ring<=p.RingDiameter/2)e.Add("内环直径、范围或间距无效。");else if(RingCount(p)>1000)e.Add("内环超过 1000 道，超出第一版建模容量。");}
        if(!p.CreateHost&&p.HostId<=0)e.Add("请先选择并核验已有混凝土宿主，或启用新建宿主。");
        return e;
    }
    public static CagePlan Build(CageInput p)
    {
        var errors=Validate(p);if(errors.Count>0)throw new ArgumentException(string.Join("\n",errors));
        var r=Radii(p);var plan=new CagePlan{MainRadius=r.Main,SpiralRadius=r.Spiral,RingRadius=r.Ring};
        Point3 Pt(double rad,double a,double z)=>new(p.X+rad*Math.Cos(a),p.Y+rad*Math.Sin(a),p.Z+z);
        for(int i=0;i<p.MainCount;i++){double a=2*Math.PI*i/p.MainCount;plan.Bars.Add(new(){Id=$"M{i+1:D3}",Kind="longitudinal",Diameter=p.MainDiameter,Radius=r.Main,Start=p.MainStart,End=p.MainEnd,Length=p.MainEnd-p.MainStart,Points=[Pt(r.Main,a,p.MainStart),Pt(r.Main,a,p.MainEnd)]});}
        var h=new BarPlan{Id="S001",Kind="helix",Diameter=p.SpiralDiameter,Radius=r.Spiral,Start=p.SpiralStart};double z0=p.SpiralStart,angle=0;
        foreach(var zone in p.Zones){double da=2*Math.PI*zone.Length/zone.Pitch;int n=Math.Max(2,(int)Math.Ceiling(da/(Math.PI/24)));h.Pieces.Add(new(){Start=z0,End=z0+zone.Length,Pitch=zone.Pitch,StartAngle=angle});for(int j=0;j<=n;j++){if(j==0&&h.Points.Count>0)continue;h.Points.Add(Pt(r.Spiral,angle+da*j/n,z0+zone.Length*j/n));}h.Length+=zone.Length*Math.Sqrt(1+Math.Pow(2*Math.PI*r.Spiral/zone.Pitch,2));angle+=da;z0+=zone.Length;}
        h.End=z0;plan.Bars.Add(h);
        for(int i=0;i<RingCount(p);i++){double z=p.RingStart+i*p.RingSpacing;plan.Bars.Add(new(){Id=$"R{i+1:D3}",Kind="ring",Diameter=p.RingDiameter,Radius=r.Ring,Start=z,End=z,Length=2*Math.PI*r.Ring,Points=[Pt(r.Ring,0,z)]});}
        return plan;
    }
}
