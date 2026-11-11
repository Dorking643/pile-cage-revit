using System.IO;
using System.Text.Json;

namespace PileCage;
public sealed class RuleRecord
{
    public string Id {get;set;}="";
    public string Name {get;set;}="";
    public string SourceType {get;set;}="Standard";
    public string Source {get;set;}="待核实原文来源";
    public string Clause {get;set;}="待确认";
    public string Summary {get;set;}="尚未核实原文，不作为规范合格判据。";
    public string Applicability {get;set;}="待项目确认";
    public string Parameter {get;set;}="";
    public string Operator {get;set;}=">=";
    public double? Limit {get;set;}
    public string Unit {get;set;}="mm";
    public bool Enabled {get;set;}
    public bool OriginalVerified {get;set;}
    public bool ProjectApplicable {get;set;}
    public string Action {get;set;}="Reference";
    public override string ToString()=>$"{(Enabled?"☑":"☐")} {Name}";
}
public sealed class RuleFinding {public string RuleId {get;set;}="";public string Severity {get;set;}="Unchecked";public string Message {get;set;}="";}
public static class RuleStore
{
    public static string Path=>System.IO.Path.Combine(Files.Root,"config","rules.json");
    public static List<RuleRecord> Defaults()=>[
        new(){Id="STD-MAIN",Name="主筋布置及净距",Parameter="MainClearSpacing"},
        new(){Id="STD-SPIRAL-D",Name="螺旋箍筋直径",Parameter="SpiralDiameter"},
        new(){Id="STD-SPIRAL-P",Name="螺旋间距及加密条件",Parameter="SpiralMaxPitch"},
        new(){Id="STD-RING",Name="内加劲环直径与布置",Parameter="RingSpacing"},
        new(){Id="STD-COVER",Name="保护层及测量基准",Parameter="Cover"},
    ];
    public static List<RuleRecord> Load()
    {
        if(!File.Exists(Path)){var d=Defaults();Save(d);return d;}
        return JsonSerializer.Deserialize<List<RuleRecord>>(File.ReadAllText(Path),Files.Json)??throw new InvalidDataException("规则文件不是有效列表。");
    }
    public static void Save(List<RuleRecord> rules)
    {
        if(rules.Select(r=>r.Id).Distinct().Count()!=rules.Count)throw new InvalidDataException("规则编号必须唯一。");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path+".tmp",JsonSerializer.Serialize(rules,Files.Json));File.Move(Path+".tmp",Path,true);
    }
}
public static class RuleChecker
{
    public static List<RuleFinding> Check(CageInput p,List<RuleRecord> rules)
    {
        var f=new List<RuleFinding>();var r=CagePlanner.Radii(p);
        var values=new Dictionary<string,double>{["Diameter"]=p.Diameter,["Height"]=p.Height,["Cover"]=p.Cover,["MainCount"]=p.MainCount,["MainDiameter"]=p.MainDiameter,["MainClearSpacing"]=p.MainCount>1?2*r.Main*Math.Sin(Math.PI/p.MainCount)-p.MainDiameter:double.PositiveInfinity,["SpiralDiameter"]=p.SpiralDiameter,["SpiralMaxPitch"]=p.Zones.Count>0?p.Zones.Max(z=>z.Pitch):double.NaN,["SpiralMinPitch"]=p.Zones.Count>0?p.Zones.Min(z=>z.Pitch):double.NaN,["RingDiameter"]=p.RingDiameter,["RingSpacing"]=p.RingSpacing};
        values["MainSurfaceCover"]=p.Diameter/2-r.Main-p.MainDiameter/2;
        foreach(var rule in rules)
        {
            void Add(string s,string m)=>f.Add(new(){RuleId=rule.Id,Severity=s,Message=$"{rule.Name}：{m}"});
            if(!rule.OriginalVerified||!rule.ProjectApplicable){Add("Unchecked","未完成该项核查（原文或项目适用条件未确认）。");continue;}
            if(!rule.Enabled){Add("Unchecked","未启用，未完成该项核查。");continue;}
            if(rule.Action=="Reference"){Add("Unchecked","仅供查阅，未执行自动判定。");continue;}
            if(rule.Parameter.StartsWith("Ring",StringComparison.Ordinal)&&!p.RingsEnabled){Add("Unchecked","当前未生成内环，未执行此检查。");continue;}
            if(!values.TryGetValue(rule.Parameter,out double v)||!rule.Limit.HasValue||!double.IsFinite(rule.Limit.Value)||rule.Operator is not (">=" or "<=" or "==")||rule.Action is not ("Block" or "Warn")){Add("Block","已启用规则配置无效，须更正规则配置后再执行。");continue;}
            bool pass=rule.Operator switch{">="=>v>=rule.Limit,"<="=>v<=rule.Limit,"=="=>Math.Abs(v-rule.Limit.Value)<1e-9,_=>false};
            Add(pass?"Pass":rule.Action,$"实际 {v:0.###}，要求 {rule.Operator} {rule.Limit:0.###} {rule.Unit}；来源 {rule.Source} {rule.Clause}。");
        }
        return f;
    }
}
