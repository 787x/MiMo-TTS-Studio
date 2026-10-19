"""Original code-drawn icon, without external image/font dependencies."""
import math, pathlib, struct, sys, zlib

def png(size):
    pixels = bytearray()
    for y in range(size):
        pixels.append(0)
        for x in range(size):
            color = (31, 88, 66, 255)
            for i in range(9):
                cx = size * (.2 + i * .075)
                height = size * (.14 + .28 * abs(math.sin(i * .9)))
                if abs(x-cx) < size*.018 and abs(y-size*.5) < height/2:
                    color = (224, 241, 200, 255)
            pixels.extend(color)
    def chunk(name, data):
        return struct.pack('!I',len(data))+name+data+struct.pack('!I',zlib.crc32(name+data))
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('!2I5B',size,size,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(pixels))+chunk(b'IEND',b'')

if __name__ == '__main__':
    root=pathlib.Path(sys.argv[1]); images=root/'app/ui/images'; images.mkdir(parents=True,exist_ok=True)
    for n in (64,256):
        data=png(n)
        (images/f'icon_{n}.png').write_bytes(data)
        (root/('ICON.PNG' if n==64 else 'ICON_256.PNG')).write_bytes(data)
