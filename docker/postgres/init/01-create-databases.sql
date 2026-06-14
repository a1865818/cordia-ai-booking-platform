-- Create the API database (Cordia_Agent is created automatically via POSTGRES_DB)
CREATE DATABASE "Cordia_API"
  WITH ENCODING 'UTF8'
       LC_COLLATE 'en_US.utf8'
       LC_CTYPE 'en_US.utf8'
       TEMPLATE template0;

GRANT ALL PRIVILEGES ON DATABASE "Cordia_Agent" TO cordia;
GRANT ALL PRIVILEGES ON DATABASE "Cordia_API" TO cordia;
