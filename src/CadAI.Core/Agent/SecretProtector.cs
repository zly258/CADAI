using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CadAI.Core
{
    /// <summary>
    /// API Key 落盘保护：用 Windows DPAPI（CurrentUser 作用域）加密后写入设置文件，
    /// 读取时解密。加密结果只能被同一 Windows 用户在同一台机器上解开——
    /// 设置文件被拷贝到别的机器/用户目录下也不会泄漏密钥。
    ///
    /// 兼容性：读取时同时接受旧版明文（历史 ai-settings.json 直接可用），
    /// 下一次 Save 自动升级为密文；解密失败视为"没有密钥"而不是抛错，
    /// 避免一次 DPAPI 异常（如用户配置文件损坏）让所有 AI 请求失败。
    /// </summary>
    internal static class SecretProtector
    {
        /// <summary>密文前缀标记：带此前缀的值按 DPAPI 密文解析，其余按明文兼容读取。</summary>
        private const string Prefix = "dpapi:";

        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;
            byte[] plain = Encoding.UTF8.GetBytes(plainText);
            try
            {
                byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(
                    plain, Entropy(), System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(encrypted);
            }
            catch (Exception ex)
            {
                // DPAPI 不可用（极少数精简系统）：退回明文并留痕，宁可降级也不能让保存失败。
                AiExceptionGuard.Log("SecretProtector.Protect", ex);
                return plainText;
            }
        }

        public static string Unprotect(string storedValue)
        {
            if (string.IsNullOrEmpty(storedValue)) return string.Empty;

            // 旧版明文：原样返回（Save 时会自动升级为密文）。
            if (!storedValue.StartsWith(Prefix, StringComparison.Ordinal))
                return storedValue;

            try
            {
                byte[] encrypted = Convert.FromBase64String(storedValue.Substring(Prefix.Length));
                byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(
                    encrypted, Entropy(), System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                // 密文损坏或属于另一个用户/机器：按"没有密钥"处理并留痕。
                AiExceptionGuard.Log("SecretProtector.Unprotect", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// 可选熵。固定值即可：目标是"拷走文件解不开"（DPAPI CurrentUser 已保证），
        /// 而不是对抗同用户下的进程——那个威胁模型需要的是凭据保险箱，超出本场景。
        /// </summary>
        private static byte[] Entropy() => Encoding.UTF8.GetBytes("CADAI.ApiKey.v1");

        /// <summary>从用户设置 JSON 的原始反序列化结果里取出 ApiKey 并解密；部署文件不含密钥时返回空串。</summary>
        public static string ReadSecret(Dictionary<string, object> values, string key)
        {
            if (values == null || !values.TryGetValue(key, out object value) || value == null) return string.Empty;
            return Unprotect(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
