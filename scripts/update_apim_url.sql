-- Ajusta la URL APIM para enrutar interoperabilidad al gateway de Colombia.
-- Reemplaza el dominio por tu despliegue real.
UPDATE interop_minsalud_config
SET apim_url = 'https://TU-GATEWAY-COLOMBIA'
WHERE activo = 1;
