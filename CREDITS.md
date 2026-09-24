# Credits

## Third-party 3D models

All three models are used under the Creative Commons Attribution 4.0 International licence
(https://creativecommons.org/licenses/by/4.0/).

| Model | Author | Source | Licence | Used as |
|---|---|---|---|---|
| "Stylized Low Poly Buildings Pack" | Mauio2369 | https://skfb.ly/pz778 | CC BY 4.0 | The HazardZone building in the AA-32 scene |
| "Drone" | ROHIT3DMODELS | https://skfb.ly/oUv78 | CC BY 4.0 | The survey UAV in the AA-32 scene |
| "Simple Low Poly Abandoned Brick Building" | jimbogies | https://skfb.ly/oSpOD | CC BY 4.0 | The ShoringSite building in the AA-32 scene |

**Changes made** (CC BY requires stating them):
- All models were re-materialed with URP Lit materials that keep their own textures, untinted.
- The drone's metallic and roughness maps were packed into one metallic-smoothness map, and its textures were downscaled to 1024 px.
- One of the pack's four buildings (`Box001`) was scaled non-uniformly to the HazardZone footprint and height, tilted 7 degrees, and ringed with procedural rubble on a glowing red base pad.
- The brick building was converted from glTF to a Unity mesh, leaving out its pavement and ground plates. It was re-materialed with URP Lit using its own base-colour and normal maps, scaled to the ShoringSite footprint, leaned 7 degrees about x and 4 about z, and placed on a glowing amber base pad.
- The drone was scaled to 11 cm and re-pivoted.

The import pipeline that applies these changes is `Assets/_HackMatrix_Agent/Editor/SetupAA32External.cs`.
