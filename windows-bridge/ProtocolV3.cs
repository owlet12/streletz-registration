// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
#if BRIDGE_CORE_TESTS
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace StreletzBridge
{
    internal static class BridgeJson
    {
        public static string Serialize(object value)
        {
#if BRIDGE_CORE_TESTS
            return JsonSerializer.Serialize(value);
#else
            return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 256 }.Serialize(value);
#endif
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
#if BRIDGE_CORE_TESTS
            using (JsonDocument doc = JsonDocument.Parse(text))
                return ConvertElement(doc.RootElement) as Dictionary<string, object>;
#else
            return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 256 }.DeserializeObject(text) as Dictionary<string, object>;
#endif
        }

#if BRIDGE_CORE_TESTS
        private static object ConvertElement(JsonElement item)
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<string, object> value = new Dictionary<string, object>();
                    foreach (JsonProperty property in item.EnumerateObject()) value[property.Name] = ConvertElement(property.Value);
                    return value;
                case JsonValueKind.Array: return item.EnumerateArray().Select(ConvertElement).ToArray();
                case JsonValueKind.String: return item.GetString();
                case JsonValueKind.Number:
                    long number;
                    return item.TryGetInt64(out number) ? (object)number : item.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
    }

    internal static class BridgeValues
    {
        public static string Text(Dictionary<string, object> value, string name)
        {
            object result;
            return value != null && value.TryGetValue(name, out result) && result != null
                ? Convert.ToString(result, CultureInfo.InvariantCulture) : "";
        }

        public static long Number(Dictionary<string, object> value, string name, long fallback = 0)
        {
            long result;
            return Int64.TryParse(Text(value, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        public static bool Flag(Dictionary<string, object> value, string name)
        {
            object result;
            return value != null && value.TryGetValue(name, out result) && result is bool && (bool)result;
        }

        public static Dictionary<string, object> Object(Dictionary<string, object> value, string name)
        {
            object result;
            return value != null && value.TryGetValue(name, out result) ? result as Dictionary<string, object> : null;
        }

        public static object[] Array(Dictionary<string, object> value, string name)
        {
            object result;
            return value != null && value.TryGetValue(name, out result) ? result as object[] : null;
        }

        public static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        public static string GuidValue(string text, string description)
        {
            Guid parsed;
            if (!Guid.TryParseExact(text, "D", out parsed) || parsed == Guid.Empty)
                throw new InvalidDataException("Некорректный идентификатор: " + description + ".");
            return parsed.ToString("D");
        }

        public static string ProjectFingerprint(Dictionary<string, object> project)
        {
            object[] records = Array(project, "records");
            if (records == null) throw new InvalidDataException("В Windows-мосте не открыт проект.");
            List<string> ids = new List<string>();
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in records)
            {
                string id = Text(item as Dictionary<string, object>, "id").Trim().ToLowerInvariant();
                if (id.Length == 0 || !unique.Add(id)) throw new InvalidDataException("Повреждены идентификаторы проекта.");
                ids.Add(id);
            }
            ids.Sort(StringComparer.Ordinal);
            using (SHA256 hash = SHA256.Create())
                return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes("streletz-project-v1\n" + String.Join("\n", ids))));
        }

        public static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

        public static Dictionary<string, object> Copy(Dictionary<string, object> value)
        {
            return BridgeJson.ParseObject(BridgeJson.Serialize(value));
        }
    }

    internal static class BridgeFiles
    {
        public static void WriteNewAtomic(string path, string content)
        {
            string destination = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("Файл обновлённого проекта уже существует. Выберите новое имя копии.");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                // The same-directory move is atomic and refuses an existing
                // destination, including one created after the initial check.
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static void WriteAtomic(string path, string content, string previousBackupPath = null)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, previousBackupPath);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    // Kept open for the entire GUI lifetime, before reading pairing settings,
    // profiles or the journal. The file is deliberately not deleted on release:
    // deleting a lock pathname can let two later processes lock different files.
    internal sealed class BridgeStateLease : IDisposable
    {
        private FileStream stream;
        private BridgeStateLease(FileStream stream) { this.stream = stream; }

        internal static BridgeStateLease Acquire(string directory)
        {
            Directory.CreateDirectory(directory);
            try
            {
                return new BridgeStateLease(new FileStream(Path.Combine(directory, "bridge-state.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException)
            {
                throw new IOException("Не удалось получить исключительный доступ к состоянию Windows-моста. Закройте другой экземпляр моста для этого профиля Windows и повторите запуск. Журнал операций не изменён.");
            }
        }

        public void Dispose()
        {
            if (stream != null) { stream.Dispose(); stream = null; }
        }
    }

    internal static class ProtocolV3
    {
        public const int MaximumLineLength = 65536;
        public const int MaximumResponseLineLength = 24 * 1024 * 1024;
        public const int MaximumRequestPlaintext = 48000;
        public const int MaximumResponsePlaintext = 16 * 1024 * 1024;

        private static int PlaintextLimit(string direction)
        {
            if (direction == "request") return MaximumRequestPlaintext;
            if (direction == "response") return MaximumResponsePlaintext;
            throw new ArgumentException("direction");
        }

        public static string NormalizePairingCode(string code)
        {
            string value = (code ?? "").Trim().Replace("-", "").ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-F]{16}$") || value == "0000000000000000")
                throw new InvalidDataException("Неверный код подключения Windows-моста.");
            return value;
        }

        private static byte[] Digest(string code)
        {
            using (SHA512 hash = SHA512.Create())
                return hash.ComputeHash(Encoding.UTF8.GetBytes("StreletzBridge/v3\n" + NormalizePairingCode(code)));
        }

        public static byte[] JournalIdentityKey(string code)
        {
            using (SHA256 hash = SHA256.Create())
                return hash.ComputeHash(Encoding.UTF8.GetBytes("StreletzBridge/journal\n" + NormalizePairingCode(code)));
        }

        private static byte[] AuthenticatedBytes(string direction, byte[] iv, byte[] cipher)
        {
            if (direction != "request" && direction != "response") throw new ArgumentException("direction");
            byte[] prefix = Encoding.UTF8.GetBytes("StreletzBridge/v3/" + direction + "\n");
            byte[] value = new byte[prefix.Length + iv.Length + cipher.Length];
            Buffer.BlockCopy(prefix, 0, value, 0, prefix.Length);
            Buffer.BlockCopy(iv, 0, value, prefix.Length, iv.Length);
            Buffer.BlockCopy(cipher, 0, value, prefix.Length + iv.Length, cipher.Length);
            return value;
        }

        public static Dictionary<string, object> Encode(Dictionary<string, object> payload, string code, string direction)
        {
            byte[] digest = Digest(code), encryptionKey = new byte[32], authenticationKey = new byte[32];
            Buffer.BlockCopy(digest, 0, encryptionKey, 0, 32);
            Buffer.BlockCopy(digest, 32, authenticationKey, 0, 32);
            byte[] iv = new byte[16], cipher;
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(iv);
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = encryptionKey; aes.IV = iv;
                byte[] plain = Encoding.UTF8.GetBytes(BridgeJson.Serialize(payload));
                if (plain.Length > PlaintextLimit(direction)) throw new InvalidDataException("Защищённое сообщение слишком большое.");
                using (ICryptoTransform transform = aes.CreateEncryptor()) cipher = transform.TransformFinalBlock(plain, 0, plain.Length);
            }
            byte[] mac;
            using (HMACSHA256 hmac = new HMACSHA256(authenticationKey)) mac = hmac.ComputeHash(AuthenticatedBytes(direction, iv, cipher));
            return new Dictionary<string, object> {
                { "protocol", 3 }, { "type", "bridge-" + direction },
                { "iv", Convert.ToBase64String(iv) }, { "data", Convert.ToBase64String(cipher) }, { "mac", Convert.ToBase64String(mac) }
            };
        }

        public static Dictionary<string, object> Decode(Dictionary<string, object> envelope, string code, string direction)
        {
            if (BridgeValues.Number(envelope, "protocol") != 3 || BridgeValues.Text(envelope, "type") != "bridge-" + direction)
                throw new InvalidDataException("Неподдерживаемая версия защищённого протокола.");
            byte[] iv, cipher, supplied;
            try
            {
                iv = Convert.FromBase64String(BridgeValues.Text(envelope, "iv"));
                cipher = Convert.FromBase64String(BridgeValues.Text(envelope, "data"));
                supplied = Convert.FromBase64String(BridgeValues.Text(envelope, "mac"));
            }
            catch (FormatException) { throw new InvalidDataException("Повреждён защищённый запрос."); }
            if (iv.Length != 16 || cipher.Length == 0 || cipher.Length > PlaintextLimit(direction) + 16 || cipher.Length % 16 != 0 || supplied.Length != 32)
                throw new InvalidDataException("Повреждён защищённый запрос.");
            byte[] digest = Digest(code), encryptionKey = new byte[32], authenticationKey = new byte[32];
            Buffer.BlockCopy(digest, 0, encryptionKey, 0, 32);
            Buffer.BlockCopy(digest, 32, authenticationKey, 0, 32);
            byte[] expected;
            using (HMACSHA256 hmac = new HMACSHA256(authenticationKey)) expected = hmac.ComputeHash(AuthenticatedBytes(direction, iv, cipher));
            int mismatch = 0;
            for (int index = 0; index < expected.Length; index++) mismatch |= expected[index] ^ supplied[index];
            if (mismatch != 0) throw new UnauthorizedAccessException("Неверный код подключения или повреждённое сообщение.");
            byte[] plaintext;
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = encryptionKey; aes.IV = iv;
                using (ICryptoTransform transform = aes.CreateDecryptor()) plaintext = transform.TransformFinalBlock(cipher, 0, cipher.Length);
            }
            if (plaintext.Length > PlaintextLimit(direction)) throw new InvalidDataException("Защищённое сообщение слишком большое.");
            Dictionary<string, object> value = BridgeJson.ParseObject(new UTF8Encoding(false, true).GetString(plaintext));
            if (value == null || BridgeValues.Number(value, "protocol") != 3)
                throw new InvalidDataException("Внутри сообщения находится неподдерживаемый протокол.");
            return value;
        }

        public static Dictionary<string, object> Response(string requestId, bool ok, string message, string errorCode = null)
        {
            Dictionary<string, object> response = new Dictionary<string, object> {
                { "protocol", 3 }, { "type", "response" }, { "requestId", requestId ?? "" },
                { "ok", ok }, { "message", message ?? "" }, { "serverTime", BridgeValues.NowMs() }
            };
            if (!String.IsNullOrEmpty(errorCode)) response["errorCode"] = errorCode;
            return response;
        }
    }
}
