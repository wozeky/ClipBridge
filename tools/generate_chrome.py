"""Deterministic, original chrome material; no frames from the supplied reference are distributed.
Requires numpy, Pillow and ffmpeg. Produces a periodic 10 s physically shaded height field.
The video is decoded by Windows Media Foundation/WPF; no per-frame Python work at runtime.
"""
from pathlib import Path
import numpy as np
from PIL import Image
import subprocess
import sys

out = Path(__file__).resolve().parents[1] / 'src/ClipBridge.App/Assets'
w,h = 960,720
y,x = np.mgrid[-1:1:complex(h), -1.333:1.333:complex(w)]

def frame(t):
    a = t * 2*np.pi/10
    # An asymmetrical twisted sheet, with broad crests and closely folded chrome edges.
    u = x - .13*np.sin(a) - .1
    v = y - .04*np.cos(a)
    bend = .39*np.sin(u*3.7+a) + .13*np.sin(u*6.1-a)
    q = (v-bend) * (5.5+1.3*np.cos(u*2-a))
    z = .32*np.sin(q) + .075*np.sin(q*2.15 + u*1.2 + .3*np.sin(a))
    z += .06*np.sin(u*4.5 + v*2 + a)
    dy,dx = np.gradient(z,2/h,2.666/w)
    nx,ny,nz = -dx,-dy,np.ones_like(x)
    n = np.sqrt(nx*nx+ny*ny+nz*nz)
    nx,ny,nz = nx/n,ny/n,nz/n
    # Mirror-reflected studio strip lights on a nearly black environment.
    rx,ry,rz = 2*nx*nz,2*ny*nz,2*nz*nz-1
    band = rx*.38 + ry*.85 + rz*.24
    light = .012 + .13*np.exp(-((band-.26)/.22)**2)
    light += .7*np.exp(-((band-.53)/.035)**2)
    light += .23*np.exp(-((band+.55)/.055)**2)
    light += .06*np.exp(-((rx+.2)/.18)**2)
    # Reference's large negative space: illuminate only the central diagonal liquid sheet.
    mask = np.exp(-((u/.82)**4 + ((v-.1)/.66)**4)*1.15)
    mask *= np.exp(-((v-bend)/.62)**4*.8)
    rgb = np.clip(.008 + light*mask,0,1)
    return (np.stack([rgb*.97,rgb*.985,rgb],axis=-1)*255).astype('uint8')

out.mkdir(parents=True,exist_ok=True)
Image.fromarray(frame(0)).save(out/'ChromePoster.png')
if '--poster' not in sys.argv:
    p = subprocess.Popen(['ffmpeg','-y','-loglevel','error','-f','rawvideo','-pix_fmt','rgb24',
        '-s',f'{w}x{h}','-r','30','-i','-','-an','-c:v','libx264','-preset','slow','-crf','19',
        '-vf','scale=in_range=full:out_range=full:out_color_matrix=bt709','-color_range','pc',
        '-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709','-pix_fmt','yuv420p','-movflags','+faststart',str(out/'ChromeLoop.mp4')],stdin=subprocess.PIPE)
    for i in range(300):
        p.stdin.write(frame(i/30).tobytes())
    p.stdin.close()
    if p.wait(): raise RuntimeError('ffmpeg failed')
