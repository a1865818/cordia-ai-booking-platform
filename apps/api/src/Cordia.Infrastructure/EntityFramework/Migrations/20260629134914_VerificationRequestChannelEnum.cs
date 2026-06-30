using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class VerificationRequestChannelEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    CREATE TYPE public.verification_request_channel_enum AS ENUM ('email', 'sms');
                EXCEPTION
                    WHEN duplicate_object THEN NULL;
                END
                $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'verification_challenges'
                          AND column_name = 'channel'
                          AND data_type = 'integer'
                    ) THEN
                        ALTER TABLE public.verification_challenges
                            ALTER COLUMN channel TYPE public.verification_request_channel_enum
                            USING (
                                CASE channel
                                    WHEN 0 THEN 'email'::public.verification_request_channel_enum
                                    WHEN 1 THEN 'sms'::public.verification_request_channel_enum
                                    ELSE 'email'::public.verification_request_channel_enum
                                END
                            );
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
