using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using RamMx.Core;

namespace RamMx.Windows;

public sealed class SignatureService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FileInfoNative { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public nint File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public nint Policy, Sip; public uint UiChoice, Revocation, UnionChoice; public nint File;
        public uint StateAction; public nint StateData, Url; public uint ProviderFlags, UiContext; public nint SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    public Inspection Inspect(string name, string path)
    {
        var now = DateTimeOffset.Now; var evidence = new List<Evidence>();
        if (!File.Exists(path)) return new(name, path, SignatureState.Unavailable, "ไม่ทราบ", [new("เข้าถึงไฟล์ไม่ได้", "Process อาจจบไปแล้วหรือสิทธิ์ไม่พอ", "ข้อมูลไม่ครบ", now)]);
        var native = new FileInfoNative { Size = (uint)Marshal.SizeOf<FileInfoNative>(), Path = path };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoNative>());
        SignatureState state; string publisher = "ไม่ทราบ";
        try
        {
            Marshal.StructureToPtr(native, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer, StateAction = 1, ProviderFlags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            int code = WinVerifyTrust(new nint(-1), ref action, ref data);
            data.StateAction = 2; WinVerifyTrust(new nint(-1), ref action, ref data);
            state = code == 0 ? SignatureState.Valid : unchecked((uint)code) == 0x800B0100 ? SignatureState.Unsigned : unchecked((uint)code) is 0x80096010 or 0x800B010C ? SignatureState.Invalid : SignatureState.Unavailable;
            if (state == SignatureState.Valid)
            {
                // X509CertificateLoader consumes certificate files, not PE Authenticode containers.
                // Trust has already been checked with WinVerifyTrust; this legacy API extracts display metadata only.
#pragma warning disable SYSLIB0057
                try { using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); publisher = cert.GetNameInfo(X509NameType.SimpleName, false); }
                catch (System.Security.Cryptography.CryptographicException) { publisher = "ยืนยัน trust ได้ แต่ชื่อ signer อ่านไม่ได้"; }
#pragma warning restore SYSLIB0057
                evidence.Add(new("ตรวจสอบ Authenticode ได้", $"ผู้ลงนาม: {publisher}; ไม่ใช่การรับรองพฤติกรรมปลอดภัย", "ข้อมูลตัวตน", now));
            }
            else evidence.Add(new(state == SignatureState.Unsigned ? "ไม่พบ embedded signature" : "ยังยืนยันลายเซ็นไม่ได้",
                $"รหัส 0x{unchecked((uint)code):X8}; ตรวจแบบ cache-only/offline อาจไม่พบ certificate chain หรือ catalog signature; ไม่ใช่ malware verdict", state == SignatureState.Invalid ? "ควรตรวจสอบ" : "ข้อมูลไม่ครบ", now));
        }
        finally { Marshal.DestroyStructure<FileInfoNative>(pointer); Marshal.FreeHGlobal(pointer); }
        if (path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)) evidence.Add(new("ทำงานจากโฟลเดอร์ชั่วคราว", "พบได้ทั้งใน installer ที่ถูกต้องและโปรแกรมที่ควรตรวจสอบ ต้องดูร่วมกับแหล่งที่มา", "ตรวจบริบท", now));
        evidence.Add(new("ข้อมูลทรัพยากร", "การใช้ RAM มากหรือทำงานเบื้องหลังเพียงอย่างเดียวไม่ใช่หลักฐาน malware", "ข้อจำกัด", now));
        return new(name, path, state, publisher, evidence);
    }
}
