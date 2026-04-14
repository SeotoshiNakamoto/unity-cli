using System.Security.Cryptography;
using System.Text;
using UnityEngine.UIElements;

namespace UnityCliConnector.UIToolkit
{
    internal static class StableIdGenerator
    {
        internal static string GenerateId(VisualElement element, string windowTitle, string hierarchyPath, string label)
        {
            if (!string.IsNullOrEmpty(element.name))
                return "id:" + element.name;

            var input = $"{windowTitle}/{hierarchyPath}/{element.GetType().Name}/{label}";
            var hash = ComputeHash(input);
            return "auto:" + hash;
        }

        static string ComputeHash(string input)
        {
            using (var sha1 = SHA1.Create())
            {
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
