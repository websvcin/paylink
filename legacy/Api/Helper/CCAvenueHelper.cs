using System;
using System.Security.Cryptography;
using System.Text;

public class CCAvenueHelper
{
    public static string Encrypt(string plainText, string key)
    {
        byte[] keyBytes = StringToByteArray(key);
        using (Aes aes = Aes.Create())
        {
            aes.Key = keyBytes;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();

            using (var encryptor = aes.CreateEncryptor())
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                byte[] combinedBytes = new byte[aes.IV.Length + encryptedBytes.Length];

                Buffer.BlockCopy(aes.IV, 0, combinedBytes, 0, aes.IV.Length);
                Buffer.BlockCopy(encryptedBytes, 0, combinedBytes, aes.IV.Length, encryptedBytes.Length);

                return BitConverter.ToString(combinedBytes).Replace("-", "").ToLower();
            }
        }
    }

    public static string Decrypt(string encryptedText, string key)
    {
        byte[] keyBytes = StringToByteArray(key);
        byte[] encryptedBytes = StringToByteArray(encryptedText);

        using (Aes aes = Aes.Create())
        {
            aes.Key = keyBytes;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            byte[] iv = new byte[16];
            byte[] cipherText = new byte[encryptedBytes.Length - 16];

            Buffer.BlockCopy(encryptedBytes, 0, iv, 0, iv.Length);
            Buffer.BlockCopy(encryptedBytes, iv.Length, cipherText, 0, cipherText.Length);

            aes.IV = iv;

            using (var decryptor = aes.CreateDecryptor())
            {
                byte[] decryptedBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
        }
    }

    private static byte[] StringToByteArray(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
