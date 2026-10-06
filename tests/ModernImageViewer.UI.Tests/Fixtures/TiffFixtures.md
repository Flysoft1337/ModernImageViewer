# TIFF fixtures

Project-owned samples generated deterministically by `TiffFixtures.cs`. No downloaded files,
third-party artwork or native encoder is required. The four baseline byte streams are fixed by
`SamplesHaveFixedHashes`.

Little-endian baseline TIFF, RGB8, one uncompressed strip per IFD. Pixel RGB values are
`(pageIndex * 19 + rawX + rawY, rawY * 29, rawX * 17)`, modulo 256. Alpha is opaque.

| Sample | Pages | Bytes | SHA-256 |
| --- | --- | --- | --- |
| Single | 8x5, orientation 1 | 272 | AAE3EDEEFCA4EA41241E24D6FA18D43A55309DF6FE45AC0BFE39098845892D54 |
| Multiple | 8x5, 5x3, 2050x4 | 25205 | 7EF9C61A07DD06A4C54BEDB6A75DAEDB3081A8B7D3D29C589C6CC595496AE6A7 |
| Orientations | (8+i)x(5+i), orientations i=1..8 | 4136 | 382BCBDCA96B3428BAB588895E5DFF4253A2A0035F11F678EFCC0BFAB1AD5DD4 |
| BrokenPage | readable 8x5, 5x3 with strip offset beyond EOF | 461 | 340CC6ACC763FCCA0A41C85E385D5BE7A8862C3849067980E1E83DFA2CD28BA5 |

Additional generated cases cover a 6000x1000 full detail page, source dimensions above 32768 or 100MP,
10001 pages and an input above 256MiB. The broken-strip case preserves both page descriptors;
reading page two fails, and reading page one again returns identical pixels.

The blocked-work test substitutes a controlled worker function through reflection, keeping
the real session queue, Detail lease, cancellation, late-result disposal and native context
teardown. It proves lifecycle ordering independently of native decoding speed; real WIC
decoding, orientation and region pixels are exercised by the other tests.

When `MIV_FORMAT_FIXTURE_DIRECTORY` is set, the successful random-page pixel test exports
the fixed `Multiple` sample as `animation-pages.tiff` for CI animation/page observation.
Oversized and metadata-limit samples are not exported to the observation directory.
