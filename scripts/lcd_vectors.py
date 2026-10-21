# Reference LCD headers (upstream crypto.rs) built with the OpenSSL CLI, independently of the C# code.
import hashlib, struct, subprocess

KEY = b"slv3tuzx".hex()

def des(plain: bytes) -> bytes:
    return subprocess.run(["openssl", "enc", "-des-cbc", "-K", KEY, "-iv", KEY, "-provider", "legacy", "-provider", "default"],
                          input=plain, capture_output=True, check=True).stdout

def plain(size, command, ts, params):
    p = bytearray(size); p[0] = command; p[2] = 0x1A; p[3] = 0x6D
    p[4:8] = struct.pack("<I", ts); p[8:8 + len(params)] = params
    return bytes(p)

def legacy(command, ts, params=b""):          # PacketBuilder::build — 504 → 512
    out = des(plain(504, command, ts, params)); assert len(out) == 512; return out

def winusb(command, ts, params=b""):          # PacketBuilder::build_winusb — 500 → 504 + tail
    out = des(plain(500, command, ts, params)); assert len(out) == 504
    return out + bytes(6) + b"\xA1\x1A"

ts = 0x01020304
cases = [
    ("fan 0A", legacy(0x0A, ts)),
    ("fan 0F 120", legacy(0x0F, ts, bytes([120]))),
    ("fan 0E 30", legacy(0x0E, ts, bytes([30]))),
    ("fan 65 0x12345", legacy(0x65, ts, struct.pack(">I", 0x12345))),
    ("pump 0F 30", winusb(0x0F, ts, bytes([30]))),
    ("pump 0E 80", winusb(0x0E, ts, bytes([80]))),
    ("pump 65 0x12345", winusb(0x65, ts, struct.pack(">I", 0x12345))),
    ("pump 33 2026-10-21 12:34:56 m2", winusb(0x33, ts, bytes([0x07, 0xEA, 10, 21, 12, 34, 56, 2]))),
    ("pump 34 [0]", winusb(0x34, ts, bytes([0]))),
    ("pump 7A", winusb(0x7A, ts)),
]
for name, data in cases:
    print(f"{name}: {hashlib.sha256(data).hexdigest().upper()}")
