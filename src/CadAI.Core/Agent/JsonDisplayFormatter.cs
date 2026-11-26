using System;
using System.Text;
using System.Web.Script.Serialization;

namespace CadAI.Core
{
    internal static class JsonDisplayFormatter
    {
        public static string FormatIfJson(string value){string text=value??string.Empty,trimmed=text.Trim();if(trimmed.Length<2)return text;if(!((trimmed[0]=='{'&&trimmed[trimmed.Length-1]=='}')||(trimmed[0]=='['&&trimmed[trimmed.Length-1]==']')))return text;try{var serializer=AgentJson.New();serializer.DeserializeObject(trimmed);return PrettyPrint(trimmed);}catch{return text;}}
        internal static string PrettyPrint(string json){if(string.IsNullOrWhiteSpace(json))return json??string.Empty;var output=new StringBuilder(json.Length+Math.Min(4096,json.Length/2));int indent=0;bool inString=false,escaped=false;for(int i=0;i<json.Length;i++){char c=json[i];if(inString){output.Append(c);if(escaped)escaped=false;else if(c=='\\')escaped=true;else if(c=='"')inString=false;continue;}if(c=='"'){inString=true;output.Append(c);continue;}switch(c){case'{':case'[':output.Append(c);output.AppendLine();indent++;AppendIndent(output,indent);break;case'}':case']':output.AppendLine();indent=Math.Max(0,indent-1);AppendIndent(output,indent);output.Append(c);break;case',':output.Append(c);output.AppendLine();AppendIndent(output,indent);break;case':':output.Append(": ");break;default:if(!char.IsWhiteSpace(c))output.Append(c);break;}}return output.ToString();}
        private static void AppendIndent(StringBuilder builder,int indent){for(int i=0;i<indent;i++)builder.Append("  ");}
    }
}
