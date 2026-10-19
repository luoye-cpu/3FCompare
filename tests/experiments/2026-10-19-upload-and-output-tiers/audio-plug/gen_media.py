# Temporary test media for the bed render plug seam verification (obj/ is gitignored).
# mono_1_0.wav  : the case that used to be a silent passthrough -- one channel is a
#                 point source, so the seam must hand it to the plug as a bed.
# stereo_2_0.wav: the control that must never enter the seam (device target is 2.0).
import array
import math
import os
import struct
import wave

out = os.path.dirname(os.path.abspath(__file__))
rate = 48000
seconds = 8


def frame_value(index, freq, amplitude):
    # A tone plus a slow envelope, so peaks are not a constant and a fold-down and a
    # plug-written pair produce visibly different per-channel numbers.
    t = index / rate
    envelope = 0.55 + 0.45 * math.sin(2 * math.pi * 0.7 * t)
    value = amplitude * envelope * math.sin(2 * math.pi * freq * t)
    return max(-32768, min(32767, int(round(value * 32767))))


def write(path, channels, amplitudes, freqs):
    with wave.open(os.path.join(out, path), "wb") as handle:
        handle.setnchannels(channels)
        handle.setsampwidth(2)
        handle.setframerate(rate)
        samples = array.array("h")
        total = rate * seconds
        for index in range(total):
            for channel in range(channels):
                samples.append(frame_value(index, freqs[channel], amplitudes[channel]))
        handle.writeframesraw(samples.tobytes())
    print(path, channels, "ch", os.path.getsize(os.path.join(out, path)), "bytes")


write("mono_1_0.wav", 1, [0.35], [220.0])
write("stereo_2_0.wav", 2, [0.35, 0.30], [220.0, 330.0])

# Beds. Equal amplitude per channel and different frequencies per the fold-down note:
# the composite then reaches a dense maximum, so a poll-sampled peak level is
# reproducible between two runs of the same file instead of depending on which
# buffer happened to be the newest one when the host asked.
write("bed_5_1_6ch.wav", 6, [0.22] * 6, [110.0, 220.0, 330.0, 440.0, 550.0, 660.0])
write("bed_7_1_4_12ch.wav", 12, [0.15] * 12,
      [110.0, 165.0, 220.0, 275.0, 330.0, 385.0, 440.0, 495.0, 550.0, 605.0, 660.0, 715.0])

